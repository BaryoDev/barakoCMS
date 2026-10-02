using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Erasure;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Content.Erase;

internal class Request
{
    public Guid Id { get; set; }
}

/// <summary>
/// DELETE /api/contents/{id}/erase. Removes a content item and its history irrecoverably, for a
/// right-to-erasure request.
/// </summary>
/// <remarks>
/// Separate from a status change to Archived, and deliberately not the same verb. Archiving is
/// reversible and keeps the history; this is neither, and an endpoint whose name does not say so
/// invites someone to reach for it when they meant to unpublish.
///
/// SuperAdmin only. This is the one operation in the product that destroys the audit trail's own
/// subject matter, so it sits at the highest role rather than with content editing.
/// </remarks>
internal class Endpoint(
    IContentEraser eraser,
    IDocumentSession session,
    barakoCMS.Features.Workflows.IWorkflowRunQueue workflowRuns,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/api/contents/{id}/erase");
        // SuperAdmin was the only name in this gate, so it is the only legacy fallback, and
        // erase_content is deliberately absent from Admin's defaults.
        Definition.RequireCapability(SystemCapabilities.EraseContent, "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId);

        // Read for the content type alone. The Deleted workflows are told which entry went and what
        // type it was, never what it held.
        var contentType = (await session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct))?.ContentType;

        var found = await eraser.QueueEraseAsync(req.Id, ct);
        if (!found)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // On this session and unsaved, so the runs commit with the erasure and the audit entry.
        if (contentType is not null)
        {
            await workflowRuns.QueueDeletedAsync(req.Id, contentType, ct);
        }

        // Queued, not yet committed, and the audit entry joins it on the same session so that one
        // SaveChanges commits both. Erasing first and auditing second would leave a window where
        // the content is irrecoverably gone and the record of it failed to save, and a retry then
        // returns not found: an erasure nobody can prove happened.
        //
        // The id only. An audit entry that quotes what was erased puts the data back.
        await AuditLog.RecordAsync(session, tenant.Slug, "content.erased", userId,
            User.FindFirst("Username")?.Value,
            targetType: "content", targetId: req.Id.ToString(), ct: ct);

        await session.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
