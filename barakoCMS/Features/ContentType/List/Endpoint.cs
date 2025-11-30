using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.List;

public class Endpoint : EndpointWithoutRequest<Response>
{
    private readonly IQuerySession _session;

    public Endpoint(IQuerySession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/content-types");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var contentTypes = await _session.Query<barakoCMS.Models.ContentType>().ToListAsync(ct);

        await SendAsync(new Response 
        { 
            ContentTypes = contentTypes.ToList()
        });
    }
}
