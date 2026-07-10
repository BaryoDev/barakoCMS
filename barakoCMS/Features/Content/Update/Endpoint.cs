using FastEndpoints;
using Marten;
using barakoCMS.Models;
using System.Security.Claims;

namespace barakoCMS.Features.Content.Update;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;
    private readonly barakoCMS.Infrastructure.Services.IContentValidatorService _validator;

    public Endpoint(IDocumentSession session, barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver, barakoCMS.Infrastructure.Services.IContentValidatorService validator)
    {
        _session = session;
        _permissionResolver = permissionResolver;
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

        // Best-effort early staleness check for a friendly message when the client echoes a Version.
        var state = await _session.Events.FetchStreamStateAsync(req.Id, ct);
        if (state != null && req.Version != 0 && state.Version != req.Version) // req.Version 0 means bypass check
        {
            ThrowError(e => e.Version, "The content has been modified by another user. Please refresh and try again.", 412);
        }

        long newVersion = 0;
        try
        {
            // Atomically append with an optimistic-concurrency guard: Marten records the current
            // stream version now and rejects the commit if another writer advanced the stream first.
            await _session.Events.AppendOptimistic(req.Id, ct, events.ToArray());

            // Apply the same events to the projected document and store it in the SAME unit of work,
            // so the event stream and read model commit atomically (or roll back together).
            foreach (var evt in events)
            {
                if (evt is barakoCMS.Events.ContentUpdated updateEvt)
                {
                    existingContent.Apply(updateEvt);
                }
                else if (evt is barakoCMS.Events.ContentStatusChanged statusEvt)
                {
                    existingContent.Apply(statusEvt);
                }
            }

            _session.Store(existingContent);
            await _session.SaveChangesAsync(ct);

            newVersion = (state?.Version ?? 0) + events.Count;
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException
            || ex.GetType().Name.Contains("Concurrency")
            || ex.GetType().Name.Contains("UnexpectedMaxEventId"))
        {
            ThrowError(e => e.Version, "The content has been modified by another user. Please refresh and try again.", 412);
        }

        // Workflows are triggered out-of-band by the async WorkflowProjection reacting to the
        // committed ContentUpdated/ContentStatusChanged events — deliberately NOT awaited here.

        await SendAsync(new Response
        {
            Id = req.Id,
            Version = newVersion,
            Message = "Content updated successfully"
        });
    }
}
