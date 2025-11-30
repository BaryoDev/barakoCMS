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

        _session.Events.Append(req.Id, @event);
        await _session.SaveChangesAsync(ct);

        await SendAsync(new Response 
        { 
            Id = req.Id, 
            Message = "Content updated successfully" 
        });
    }
}
