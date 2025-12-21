using FastEndpoints;
using Marten;
using System.Security.Claims;

namespace barakoCMS.Features.Content.ChangeStatus;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;

    public Endpoint(IDocumentSession session, barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver)
    {
        _session = session;
        _permissionResolver = permissionResolver;
    }

    public override void Configure()
    {
        Put("/api/contents/{Id}/status");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirst("UserId")!.Value);
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

        _session.Events.Append(req.Id, @event);
        await _session.SaveChangesAsync(ct);

        // Reload and apply the event to the document (manual projection workaround)
        var updatedContent = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (updatedContent != null)
        {
            updatedContent.Apply(@event);
            _session.Store(updatedContent);
            await _session.SaveChangesAsync(ct);
        }

        await SendAsync(new Response
        {
            Message = $"Content status changed to {req.NewStatus}"
        });
    }
}
