using barakoCMS.Core.Interfaces;
using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using System.Security.Claims;

namespace barakoCMS.Features.Content.ChangeStatus;

internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly IContentWriter _contentWriter;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;
    private readonly IConfiguration _configuration;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
        IContentWriter contentWriter,
        IConfiguration configuration,
        ILogger<Endpoint> logger)
    {
        _contentWriter = contentWriter;
        _session = session;
        _permissionResolver = permissionResolver;
        _tenant = tenant;
        _configuration = configuration;
        _logger = logger;
    }

    public override void Configure()
    {
        Put("/api/contents/{id}/status");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null)
        {
            ThrowError("User ID claim not found");
        }

        if (!Guid.TryParse(userIdClaim.Value, out var userId))
        {
            ThrowError("Invalid User ID format");
        }

        var user = await _session.LoadAsync<barakoCMS.Models.User>(userId, ct);

        // Check if content exists
        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (content == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // PERMISSION CHECK
        // Treating status change as an "Update" action.
        if (user == null || !await _permissionResolver.CanPerformActionAsync(user, content.ContentType, "update", content, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // Which of the two request shapes is correct depends on the content type, which the
        // validator cannot see. A type with a lifecycle takes a named transition; every type that
        // exists today has none and takes a status.
        var definition = await _session.Query<barakoCMS.Models.ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == content.ContentType, ct);
        var lifecycle = definition?.Lifecycle;

        if (lifecycle is not null)
        {
            await HandleTransitionAsync(req, content, lifecycle, user, userId, ct);
            return;
        }

        if (req.Transition is not null)
        {
            ThrowError($"Content type '{content.ContentType}' declares no lifecycle, so it takes NewStatus rather than a transition.", 400);
        }

        var newStatus = req.NewStatus!.Value;

        // A no-op request appends nothing. ContentStatusChanged carries only the new status, so the
        // workflow projection cannot tell "moved to Published" from "set to Published while already
        // Published": a second event fires every Published workflow again, and the confirmation
        // email goes out twice for a double-clicked button or a client retry. The Update slice has
        // always guarded this; this one did not. It also keeps transitions that changed nothing out
        // of the stream, which is the source of truth for history and replay.
        if (content.Status == newStatus)
        {
            await Send.ResponseAsync(new Response
            {
                Message = $"Content status is already {newStatus}"
            });
            return;
        }

        var @event = new barakoCMS.Events.ContentStatusChanged(req.Id, newStatus, userId);

        // Append the event AND update the read-model document in one transaction so they can't
        // diverge. Workflows fire out-of-band via the async WorkflowProjection, which is driven off the
        // event stream — so the append is what makes "Published" workflows actually run.
        //
        // Under an expected-version check rather than a plain append: this is a whole-document write
        // built from a document loaded at the top of the request, so an unguarded append would let it
        // overwrite a scheduler transition or an edit that landed in between.
        try
        {
            await _contentWriter.AppendOptimisticAsync(content, new[] { @event }, ct);

            // There's no content-delete endpoint in barakoCMS today, and archiving is the closest
            // destructive-equivalent action, so it's what gets audited here rather than every routine
            // draft-to-published transition, which would just be noise.
            if (newStatus == barakoCMS.Models.ContentStatus.Archived)
            {
                await AuditLog.RecordAsync(_session, _tenant.Slug, "content.archived", userId, user.Username,
                    targetType: content.ContentType, targetId: content.Id.ToString(), ct: ct);
            }

            await _session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException
            || ex.GetType().Name.Contains("Concurrency")
            || ex.GetType().Name.Contains("UnexpectedMaxEventId"))
        {
            // 409 rather than the 412 the update endpoint returns: nothing here was conditional on a
            // version the client sent, so there is no precondition to have failed.
            ThrowError("The content was changed by another writer. Please refresh and try again.", 409);
        }

        await Send.ResponseAsync(new Response
        {
            Message = $"Content status changed to {newStatus}"
        });
    }

    /// <summary>
    /// Performs a named transition against the content type's own lifecycle.
    /// </summary>
    /// <remarks>
    /// The refusal is server side, not a button the admin declines to draw. CLAUDE.md section 9 is
    /// explicit that hiding a control is not access control, and a lifecycle that only the UI
    /// enforces is a lifecycle any HTTP client can ignore.
    ///
    /// ContentStatus is untouched here. The enum decides whether public delivery serves an entry and
    /// a custom lifecycle decides nothing about that, so an invoice moving from Submitted to Approved
    /// does not become publicly visible as a side effect. Conflating the two is the shortcut that
    /// makes a system nobody can explain.
    ///
    /// Lifecycle:EnforceTransitions exists because a deployment adopting lifecycles has entries that
    /// predate the rules, and refusing every edit to them is not a migration path. Off logs the
    /// violation and allows it, which is a deliberate escape hatch rather than an oversight, and it
    /// defaults to on.
    /// </remarks>
    private async Task HandleTransitionAsync(
        Request req,
        barakoCMS.Models.Content content,
        barakoCMS.Models.LifecycleDefinition lifecycle,
        barakoCMS.Models.User user,
        Guid userId,
        CancellationToken ct)
    {
        if (req.NewStatus.HasValue)
        {
            ThrowError($"Content type '{content.ContentType}' declares a lifecycle, so it takes Transition rather than NewStatus.", 400);
        }

        // An entry written before the type declared a lifecycle has no state. It starts at the
        // declared initial state rather than being unmovable, because the alternative is content
        // that can never be transitioned and no way to fix it short of editing the database.
        var currentState = content.LifecycleState ?? lifecycle.InitialState;

        var transition = lifecycle.Transitions.FirstOrDefault(
            t => string.Equals(t.Name, req.Transition, StringComparison.OrdinalIgnoreCase));

        if (transition is null)
        {
            var available = lifecycle.Transitions.Count == 0
                ? "(none)"
                : string.Join(", ", lifecycle.Transitions.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
            ThrowError($"'{req.Transition}' is not a transition on '{content.ContentType}'. Declared transitions: {available}.", 400);
            return;
        }

        if (!string.Equals(transition.From, currentState, StringComparison.OrdinalIgnoreCase))
        {
            var enforce = _configuration.GetValue("Lifecycle:EnforceTransitions", true);
            var message = $"'{transition.Name}' moves {transition.From} to {transition.To}, and this entry is {currentState}.";

            if (enforce)
            {
                ThrowError(message, 409);
                return;
            }

            // Recorded at warning level rather than passed over. The setting exists to let existing
            // data through, and an operator who turned it on should be able to see what it let
            // through and how often.
            _logger.LogWarning(
                "Lifecycle:EnforceTransitions is off and permitted an out-of-order transition on {ContentId}: {Message}",
                content.Id, message);
        }

        var transitioned = new barakoCMS.Events.ContentTransitioned(
            content.Id, transition.Name, currentState, transition.To, userId);

        try
        {
            await _contentWriter.AppendOptimisticAsync(content, new object[] { transitioned }, ct);

            await AuditLog.RecordAsync(_session, _tenant.Slug, $"content.transitioned", userId, user.Username,
                targetType: content.ContentType, targetId: content.Id.ToString(),
                metadata: new() { ["transition"] = transition.Name, ["from"] = currentState, ["to"] = transition.To },
                ct: ct);

            await _session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException
            || ex.GetType().Name.Contains("Concurrency")
            || ex.GetType().Name.Contains("UnexpectedMaxEventId"))
        {
            ThrowError("The content was changed by another writer. Please refresh and try again.", 409);
        }

        await Send.ResponseAsync(new Response
        {
            Message = $"{transition.Name} moved this entry to {transition.To}",
        });
    }
}
