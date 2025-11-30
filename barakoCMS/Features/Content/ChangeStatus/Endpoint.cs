using FastEndpoints;
using Marten;
using System.Security.Claims;

namespace barakoCMS.Features.Content.ChangeStatus;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Put("/api/contents/{Id}/status");
        Claims("UserId");
        Roles("Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirst("UserId")!.Value);

        // Check if content exists
        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (content == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        var @event = new barakoCMS.Events.ContentStatusChanged(req.Id, req.NewStatus, userId);

        _session.Events.Append(req.Id, @event);
        await _session.SaveChangesAsync(ct);

        await SendAsync(new Response 
        { 
            Message = $"Content status changed to {req.NewStatus}" 
        });
    }
}
