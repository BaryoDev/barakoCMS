using barakoCMS.Core.Interfaces;
using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using System.Security.Claims;

namespace barakoCMS.Features.Content.ChangeStatus;

public class Endpoint : Endpoint<Request, Response>
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
        Put("/api/contents/{Id}/status");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null)
        {
            await SendAsync(new Response { Message = "User ID claim not found" }, 400, ct);
            return;
        }

        if (!Guid.TryParse(userIdClaim.Value, out var userId))
        {
            await SendAsync(new Response { Message = "Invalid User ID format" }, 400, ct);
            return;
        }

        var user = await _session.LoadAsync<barakoCMS.Models.User>(userId, ct);

        // Check if content exists
        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (content == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        // PERMISSION CHECK
        // Treating status change as an "Update" action.
        if (user == null || !await _permissionResolver.CanPerformActionAsync(user, content.ContentType, "update", content, ct))
        {
            await SendForbiddenAsync(ct);
            return;
        }

        var @event = new barakoCMS.Events.ContentStatusChanged(req.Id, req.NewStatus, userId);

        // Append the event AND update the read-model document in one transaction so they can't
        // diverge. Workflows fire out-of-band via the async WorkflowProjection, which is driven off the
        // event stream — so the append is what makes "Published" workflows actually run.
        _contentWriter.Append(content, @event);

        // There's no content-delete endpoint in barakoCMS today — archiving is the closest
        // destructive-equivalent action, so it's what gets audited here rather than every routine
        // draft→published transition, which would just be noise.
        if (req.NewStatus == barakoCMS.Models.ContentStatus.Archived)
        {
            await AuditLog.RecordAsync(_session, _tenant.Slug, "content.archived", userId, user.Username,
                targetType: content.ContentType, targetId: content.Id.ToString(), ct: ct);
        }

        await _session.SaveChangesAsync(ct);

        await SendAsync(new Response
        {
            Message = $"Content status changed to {req.NewStatus}"
        });
    }
}
