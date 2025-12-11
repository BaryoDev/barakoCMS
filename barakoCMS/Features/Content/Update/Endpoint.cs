using FastEndpoints;
using Marten;
using barakoCMS.Models;
using System.Security.Claims;

namespace barakoCMS.Features.Content.Update;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Put("/api/contents/{id}");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirst("UserId")!.Value);

        var @event = new barakoCMS.Events.ContentUpdated(req.Id, req.Data, userId);

        try
        {
            // Explicit Concurrency Check
            var state = await _session.Events.FetchStreamStateAsync(req.Id, ct);
            if (state != null && state.Version != req.Version)
            {
                ThrowError(e => e.Version, "The content has been modified by another user. Please refresh and try again.", 412);
            }

            // Append (Optimistic constraint handled by check above)
            _session.Events.Append(req.Id, @event);
            await _session.SaveChangesAsync(ct);
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
