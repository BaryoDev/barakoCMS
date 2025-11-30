using FastEndpoints;
using Marten;
using barakoCMS.Models;
using System.Security.Claims;

namespace barakoCMS.Features.Content.Create;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/contents");
        Claims("UserId");
        Roles("Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirst("UserId")!.Value);

        var contentId = Guid.NewGuid();
        var @event = new barakoCMS.Events.ContentCreated(contentId, req.ContentType, req.Data, userId);

        _session.Events.StartStream<barakoCMS.Models.Content>(contentId, @event);
        await _session.SaveChangesAsync(ct);

        await SendAsync(new Response 
        { 
            Id = contentId, 
            Message = "Content created successfully" 
        });
    }
}
