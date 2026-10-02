using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Logging;
using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using System.Security.Claims;

namespace barakoCMS.Features.Content.ChangeStatus;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    IContentWriter contentWriter,
    IConfiguration configuration,
    ILogger<Endpoint> logger) : Endpoint<Request, Response>
{
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

        var user = await session.LoadAsync<barakoCMS.Models.User>(userId, ct);

        var content = await session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (content == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (user == null)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // Which of the two request shapes is correct depends on the content type, which the
        // validator cannot see. A type with a lifecycle takes a named transition; every type that
        // exists today has none and takes a status.
        var definition = await session.Query<barakoCMS.Models.ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == content.ContentType, ct);
        var lifecycle = definition?.Lifecycle;

        // The permission check is deliberately not shared between the two paths.
        //
        // A status change is an edit, so it checks Update, which is what it has always done. A
        // transition is not: the whole point of #341 is that a manager approves an amount they may
        // not edit, so requiring Update as well would make the interesting half of separation of
        // duties unreachable. Checking both would have looked more careful and been strictly worse.
        //
        // The transition check lives further in, because which permission applies depends on which
        // transition was named and that is only known once the request has been matched against the
        // lifecycle.
        if (lifecycle is not null)
        {
            await HandleTransitionAsync(req, content, definition!, lifecycle, user, userId, ct);
            return;
        }

        if (req.Transition is not null)
        {
            ThrowError($"Content type '{content.ContentType}' declares no lifecycle, so it takes NewStatus rather than a transition.", 400);
        }

        // A status change is an edit of the entry, so it is governed by Update, unchanged.
        if (!await permissionResolver.CanPerformActionAsync(user, content.ContentType, "update", content, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
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

        var @event = new barakoCMS.Events.ContentStatusChanged(req.Id, newStatus, userId, DateTime.UtcNow);

        // Append the event AND update the read-model document in one transaction so they can't
        // diverge. Workflows fire out-of-band via the async WorkflowProjection, which is driven off the
        // event stream — so the append is what makes "Published" workflows actually run.
        //
        // Under an expected-version check rather than a plain append: this is a whole-document write
        // built from a document loaded at the top of the request, so an unguarded append would let it
        // overwrite a scheduler transition or an edit that landed in between.
        try
        {
            await contentWriter.AppendOptimisticAsync(content, new[] { @event }, ct);

            // There's no content-delete endpoint in barakoCMS today, and archiving is the closest
            // destructive-equivalent action, so it's what gets audited here rather than every routine
            // draft-to-published transition, which would just be noise.
            if (newStatus == barakoCMS.Models.ContentStatus.Archived)
            {
                await AuditLog.RecordAsync(session, tenant.Slug, "content.archived", userId, user.Username,
                    targetType: content.ContentType, targetId: content.Id.ToString(), ct: ct);
            }

            await session.SaveChangesAsync(ct);
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
    ///
    /// A transition may declare fields. The ones it requires must hold a value once the move is
    /// made, on the entry already or sent in Data, and Data may carry only the fields the transition
    /// declares. The caller needs the transition permission and not Update to send them: a reviewer
    /// who may not edit an entry still has to say why they rejected it, and the declared list is
    /// what keeps that from becoming a general edit. Sent values go through the gates an update
    /// runs (field sensitivity, the type's validation, the before-save hooks) and are recorded as a
    /// ContentUpdated beside the ContentTransitioned, in one commit.
    /// </remarks>
    private async Task HandleTransitionAsync(
        Request req,
        barakoCMS.Models.Content content,
        barakoCMS.Models.ContentTypeDefinition definition,
        barakoCMS.Models.LifecycleDefinition lifecycle,
        barakoCMS.Models.User user,
        Guid userId,
        CancellationToken ct)
    {
        if (req.NewStatus.HasValue)
        {
            ThrowError($"Content type '{content.ContentType}' declares a lifecycle, so it takes Transition rather than NewStatus.", 400);
        }

        // Nothing below this line may be reached by a caller with no rights on the type. Removing
        // the shared Update check is what makes this necessary: the refusals further down name the
        // type's declared transitions and the entry's current lifecycle state, which is a workflow
        // map handed to anyone holding a valid token. Read is the floor rather than Update, because
        // requiring Update is the coupling this whole change exists to remove.
        if (!await permissionResolver.CanPerformActionAsync(user, content.ContentType, "read", content, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
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

        // Checked here rather than at the top with the CRUD check, because which permission applies
        // depends on which transition was named, and that is only known once the request has been
        // matched against the lifecycle. It runs before the state check below, so a caller who may
        // not perform a transition is not told which state the entry is sitting in.
        //
        // A transition permission is not implied by Update. Falling back to the Update rule is the
        // obvious way to keep existing configurations working and it is the defect this exists to
        // fix: it grants approval to everyone who can edit. Undeclared means refused.
        var transitionAction = barakoCMS.Infrastructure.Services.PermissionResolver.TransitionActionPrefix + transition.Name;
        if (!await permissionResolver.CanPerformActionAsync(user, content.ContentType, transitionAction, content, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // Whether the person who raised a record may move it on is a policy, not a bug, and
        // organisations answer it differently. Refused by default because that is the direction that
        // can be relaxed later: granting it and tightening afterwards takes away something people
        // were relying on, and an approval that should not have happened cannot be undone.
        //
        // CreatedBy is what this reads, not LastModifiedBy, which moves to whoever edited last and
        // would make the check mean nothing after any edit.
        if (content.CreatedBy == userId
            && !configuration.GetValue($"Lifecycle:AllowSelfTransition:{transition.Name}", false))
        {
            logger.LogInformation(
                "Refused a self transition of {ContentId} by its creator. Set Lifecycle:AllowSelfTransition:{Transition} to allow it.",
                content.Id, transition.Name);

            await Send.ForbiddenAsync(ct);
            return;
        }

        if (!string.Equals(transition.From, currentState, StringComparison.OrdinalIgnoreCase))
        {
            var enforce = configuration.GetValue("Lifecycle:EnforceTransitions", true);
            var message = $"'{transition.Name}' moves {transition.From} to {transition.To}, and this entry is {currentState}.";

            if (enforce)
            {
                ThrowError(message, 409);
                return;
            }

            // Recorded at warning level rather than passed over. The setting exists to let existing
            // data through, and an operator who turned it on should be able to see what it let
            // through and how often.
            logger.LogWarning(
                "Lifecycle:EnforceTransitions is off and permitted an out-of-order transition on {ContentId}: {Message}",
                content.Id, message);
        }

        // After every permission check above, so a caller who may not make this move learns nothing
        // about the fields it asks for.
        var takes = TransitionFields.Resolve(transition, definition);

        if (takes.Skipped.Count > 0)
        {
            logger.LogWarning(
                "Transition {Transition} on content type {ContentType} names {Count} field(s) the type does not declare, so they were skipped: {Fields}",
                LogSafe.Value(transition.Name), LogSafe.Value(content.ContentType), takes.Skipped.Count,
                LogSafe.Value(string.Join(", ", takes.Skipped)));
        }

        Dictionary<string, object>? data = null;
        Guid? expectedDocVersion = null;

        if (takes.TakesFields && req.Data is { Count: > 0 } sent)
        {
            // The sent values are laid over a copy of the stored data, so what is validated and
            // stored is the whole entry. The version is read here so a write that lands between
            // this read and the commit fails the commit instead of being overwritten by the copy.
            expectedDocVersion = (await session.MetadataForAsync(content, ct))?.CurrentVersion;
            data = new Dictionary<string, object>(content.Data, content.Data.Comparer);

            var written = new HashSet<barakoCMS.Models.FieldDefinition>();
            var notTaken = 0;

            foreach (var (key, value) in sent)
            {
                var field = takes.Find(key);
                if (field is null)
                {
                    notTaken++;
                    continue;
                }

                if (!written.Add(field))
                {
                    AddError($"Field '{field.DisplayName}' ({field.Name}) was sent more than once, ignoring case.");
                    continue;
                }

                // Under the key the entry already stores it as, which is the one the validator reads.
                var storedKey = data.Keys.FirstOrDefault(k => TransitionFields.Matches(k, field.Name)) ?? field.Name;
                data[storedKey] = value;
            }

            // Counted and not named: the keys are whatever the caller typed.
            if (notTaken > 0)
            {
                AddError($"'{transition.Name}' takes {string.Join(", ", takes.Names)} in data, and "
                    + $"{notTaken} other {(notTaken == 1 ? "field was" : "fields were")} sent.");
            }

            ThrowIfAnyErrors();

            // A caller who may not see a field may not change it. Reverts any such field to what is
            // stored, before the required check reads it.
            await Resolve<ISensitivityService>()
                .ApplyWriteAsync(content.ContentType, data, content.Data, HttpContext, ct);
        }

        foreach (var field in TransitionFields.Blank(takes, data ?? content.Data))
        {
            AddError($"Field '{field.DisplayName}' ({field.Name}) is required by the transition '{transition.Name}'.");
        }

        ThrowIfAnyErrors();

        var events = new List<object>();

        if (data is not null)
        {
            var validation = await Resolve<barakoCMS.Infrastructure.Services.IContentValidatorService>()
                .ValidateAsync(content.ContentType, data, existing: content);
            if (!validation.IsValid)
            {
                foreach (var error in validation.Errors)
                {
                    AddError(error);
                }

                ThrowIfAnyErrors();
            }

            var hookErrors = await Resolve<barakoCMS.Infrastructure.Services.IContentLifecycleRunner>()
                .RunBeforeSaveAsync(content.ContentType, content.Id, data, content.Data, userId, ct);
            if (hookErrors.Count > 0)
            {
                foreach (var error in hookErrors)
                {
                    AddError(error);
                }

                ThrowIfAnyErrors();
            }

            var publicFields = definition.Fields
                .Where(f => f.Sensitivity == barakoCMS.Models.SensitivityLevel.Public)
                .Select(f => f.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var searchText = string.Join(
                ' ',
                data
                    .Where(kv => publicFields.Contains(kv.Key))
                    .Select(kv => kv.Value?.ToString())
                    .Where(v => !string.IsNullOrWhiteSpace(v)));

            events.Add(new barakoCMS.Events.ContentUpdated(content.Id, data, userId, searchText, DateTime.UtcNow));
        }

        events.Add(new barakoCMS.Events.ContentTransitioned(
            content.Id, transition.Name, currentState, transition.To, userId, DateTime.UtcNow));

        try
        {
            await contentWriter.AppendOptimisticAsync(content, events, ct);

            // After the append and before the commit: the writer loads the document again to store
            // it, and a version bound before that load is discarded.
            if (expectedDocVersion is { } expected)
            {
                session.UpdateExpectedVersion(content, expected);
            }

            await AuditLog.RecordAsync(session, tenant.Slug, $"content.transitioned", userId, user.Username,
                targetType: content.ContentType, targetId: content.Id.ToString(),
                metadata: new() { ["transition"] = transition.Name, ["from"] = currentState, ["to"] = transition.To },
                ct: ct);

            await session.SaveChangesAsync(ct);
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
