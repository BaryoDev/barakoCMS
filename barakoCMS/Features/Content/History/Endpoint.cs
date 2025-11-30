using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.History;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IQuerySession _session;

    public Endpoint(IQuerySession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/contents/{id}/history");
        AllowAnonymous(); // Or require auth depending on requirements
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var events = await _session.Events.FetchStreamAsync(req.Id, token: ct);

        var versions = events.Select(e => 
        {
            if (e.Data is barakoCMS.Events.ContentCreated created)
            {
                return new VersionResponse
                {
                    Id = created.Id,
                    Data = created.Data,
                    UpdatedAt = e.Timestamp.DateTime,
                    LastModifiedBy = created.CreatedBy,
                    VersionId = e.Id,
                    Timestamp = e.Timestamp
                };
            }
            else if (e.Data is barakoCMS.Events.ContentUpdated updated)
            {
                return new VersionResponse
                {
                    Id = updated.Id,
                    Data = updated.Data,
                    UpdatedAt = e.Timestamp.DateTime,
                    LastModifiedBy = updated.UpdatedBy,
                    VersionId = e.Id,
                    Timestamp = e.Timestamp
                };
            }
            return null;
        })
        .Where(v => v != null)
        .Cast<VersionResponse>()
        .ToList();

        await SendAsync(new Response 
        { 
            Versions = versions
        });
    }
}
