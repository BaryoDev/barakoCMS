using FastEndpoints;
using Marten;
using System.Security.Claims;

namespace barakoCMS.Features.Content.ChangeStatus;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;
    private readonly barakoCMS.Features.Workflows.IWorkflowEngine _workflowEngine;

    public Endpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
        barakoCMS.Features.Workflows.IWorkflowEngine workflowEngine)
    {
        _session = session;
        _permissionResolver = permissionResolver;
        _workflowEngine = workflowEngine;
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

        _session.Events.Append(req.Id, @event);

        // Apply the event to the document in the same transaction
        var updatedContent = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (updatedContent != null)
        {
            updatedContent.Apply(@event);
            _session.Store(updatedContent);

            // Single SaveChanges for atomicity - both event and document saved together
            await _session.SaveChangesAsync(ct);

            // Trigger workflow for status change (consistent with Update endpoint)
            await _workflowEngine.ProcessEventAsync(updatedContent.ContentType, "status_change", updatedContent, ct);
        }
        else
        {
            await _session.SaveChangesAsync(ct);
        }

        await SendAsync(new Response
        {
            Message = $"Content status changed to {req.NewStatus}"
        });
    }
}
