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
    private readonly barakoCMS.Infrastructure.Services.IContentValidatorService _validator;

    public Endpoint(IDocumentSession session, barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver, barakoCMS.Features.Workflows.IWorkflowEngine workflowEngine, barakoCMS.Infrastructure.Services.IContentValidatorService validator)
    {
        _session = session;
        _permissionResolver = permissionResolver;
        _workflowEngine = workflowEngine;
        _validator = validator;
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

        // DYNAMIC VALIDATION - Validate data against ContentType schema
        var validationResult = await _validator.ValidateAsync(existingContent.ContentType, req.Data);
        if (!validationResult.IsValid)
        {
            await SendAsync(new Response { Message = "Validation Failed: " + string.Join(", ", validationResult.Errors) }, 400, ct);
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

            // Save events to stream
            await _session.SaveChangesAsync(ct);

            // Reload content and manually apply events (manual projection workaround)
            var updatedContent = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
            if (updatedContent != null)
            {
                // Apply each event to the document
                foreach (var evt in events)
                {
                    if (evt is barakoCMS.Events.ContentUpdated updateEvt)
                    {
                        updatedContent.Apply(updateEvt);
                    }
                    else if (evt is barakoCMS.Events.ContentStatusChanged statusEvt)
                    {
                        updatedContent.Apply(statusEvt);
                    }
                }

                // Store the updated document
                _session.Store(updatedContent);
                await _session.SaveChangesAsync(ct);

                // WORKFLOW TRIGGER - use updated content with applied events
                await _workflowEngine.ProcessEventAsync(updatedContent.ContentType, "update", updatedContent, ct);
                if (statusChanged)
                {
                    await _workflowEngine.ProcessEventAsync(updatedContent.ContentType, "status_change", updatedContent, ct);
                }
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
