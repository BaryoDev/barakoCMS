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

        try
        {
            var contentId = Guid.NewGuid();
            var @event = new barakoCMS.Events.ContentCreated(contentId, req.ContentType, req.Data, req.Status, userId);

            _session.Events.StartStream<barakoCMS.Models.Content>(contentId, @event);
            await _session.SaveChangesAsync(ct);


            await SendAsync(new Response
            {
                Id = contentId,
                Message = "Content created successfully"
            });
        }
        catch (Exception ex)
        {
            await SendAsync(new Response { Message = $"Error creating content: {ex.Message}" }, 500, ct);
        }
    }
}
