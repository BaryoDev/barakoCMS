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

    public Endpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant, IContentWriter contentWriter)
    {
        _contentWriter = contentWriter;
        _session = session;
        _permissionResolver = permissionResolver;
        _tenant = tenant;
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
}
