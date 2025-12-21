using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.List;

public class Request : PaginatedRequest { }

public class Endpoint : Endpoint<Request, PaginatedResponse<barakoCMS.Models.ContentType>>
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

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var query = _session.Query<barakoCMS.Models.ContentType>();

        var totalCount = await query.CountAsync(ct);

        var contentTypes = await query
            .OrderBy(c => c.Name)
            .Skip(req.Skip)
            .Take(req.Take)
            .ToListAsync(ct);

        await SendAsync(new PaginatedResponse<barakoCMS.Models.ContentType>
        {
            Items = contentTypes.ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = totalCount
        }, cancellation: ct);
    }
}
