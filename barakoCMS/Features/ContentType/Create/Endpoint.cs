using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.Create;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/content-types");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var contentType = new barakoCMS.Models.ContentType
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            Slug = req.Name.ToLower().Replace(" ", "-"),
            Fields = req.Fields,
            CreatedAt = DateTime.UtcNow
        };

        _session.Store(contentType);
        await _session.SaveChangesAsync(ct);

        await SendAsync(new Response 
        { 
            Id = contentType.Id, 
            Message = "Content Type created successfully" 
        });
    }
}
