using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Erasure;
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
internal class Endpoint : Endpoint<Request>
{
    private readonly IContentEraser _eraser;
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public Endpoint(
        IContentEraser eraser,
        IDocumentSession session,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _eraser = eraser;
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Delete("/api/contents/{id}/erase");
        Roles("SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId);

        var erased = await _eraser.EraseAsync(req.Id, ct);
        if (!erased)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Recorded before the response, and recording the id only. An audit entry for an erasure
        // that quotes what was erased puts the data back.
        await AuditLog.RecordAsync(_session, _tenant.Slug, "content.erased", userId,
            User.FindFirst("Username")?.Value,
            targetType: "content", targetId: req.Id.ToString(), ct: ct);
        await _session.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
