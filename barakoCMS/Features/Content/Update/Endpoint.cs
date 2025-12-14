using FastEndpoints;
using Marten;
using barakoCMS.Models;
using System.Security.Claims;

namespace barakoCMS.Features.Content.Update;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;
    private readonly barakoCMS.Features.Workflows.IWorkflowEngine _workflowEngine;

    public Endpoint(IDocumentSession session, barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver, barakoCMS.Features.Workflows.IWorkflowEngine workflowEngine)
    {
        _session = session;
        _permissionResolver = permissionResolver;
        _workflowEngine = workflowEngine;
    }

    public override void Configure()
    {
        Put("/api/contents/{id}");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirst("UserId")!.Value);
        var user = await _session.LoadAsync<User>(userId, ct);

        var existingContent = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (existingContent == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        if (user == null || !await _permissionResolver.CanPerformActionAsync(user, existingContent.ContentType, "update", existingContent, ct))
        {
            await SendForbiddenAsync(ct);
            return;
        }

        var events = new List<object>();

        // 1. Data Update Event
        var updateEvent = new barakoCMS.Events.ContentUpdated(req.Id, req.Data, userId);
        events.Add(updateEvent);

        bool statusChanged = existingContent.Status != req.Status;

        // 2. Status Change Event (if changed)
        if (statusChanged)
        {
            var statusEvent = new barakoCMS.Events.ContentStatusChanged(req.Id, req.Status, userId);
            events.Add(statusEvent);
        }

        try
        {
            // Explicit Concurrency Check (Still useful, though Marten handles optimistic concurrency)
            var state = await _session.Events.FetchStreamStateAsync(req.Id, ct);
            if (state != null && state.Version != req.Version && req.Version != 0) // req.Version 0 means bypass check
            {
                ThrowError(e => e.Version, "The content has been modified by another user. Please refresh and try again.", 412);
            }

            // Append Events
            _session.Events.Append(req.Id, events.ToArray());

            // MANUAL PERSISTENCE (Fix for Projection Lag)
            // Apply events to existing content in memory
            existingContent.Apply(updateEvent);
            if (statusChanged)
            {
                var statusEvent = new barakoCMS.Events.ContentStatusChanged(req.Id, req.Status, userId);
                existingContent.Apply(statusEvent);
            }

            // Explicitly store the updated document
            _session.Store(existingContent);

            await _session.SaveChangesAsync(ct);

            // WORKFLOW TRIGGER
            await _workflowEngine.ProcessEventAsync(existingContent.ContentType, "update", existingContent, ct);
            if (statusChanged)
            {
                await _workflowEngine.ProcessEventAsync(existingContent.ContentType, "status_change", existingContent, ct);
            }
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("Concurrency") || ex.GetType().Name.Contains("UnexpectedMaxEventId"))
        {
            // Marten throws EventStreamUnexpectedMaxEventIdException (or ConcurrencyException)
            ThrowError(e => e.Version, "The content has been modified by another user. Please refresh and try again.", 412);
        }


        await SendAsync(new Response
        {
            Id = req.Id,
            Message = "Content updated successfully"
        });
    }
}
