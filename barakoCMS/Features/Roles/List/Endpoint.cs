using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.List;

public class Request : PaginatedRequest
{
    // No additional filters for roles list
}

public class Endpoint : Endpoint<Request, PaginatedResponse<Role>>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/roles");
        Roles("SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var query = _session.Query<Role>().AsQueryable();

        // Get total count
        var totalCount = await query.CountAsync(ct);

        // Apply sorting (by name)
        query = req.SortOrder.ToLower() == "asc"
            ? query.OrderBy(r => r.Name)
            : query.OrderByDescending(r => r.Name);

        // Apply pagination
        var roles = await query
            .Skip(req.Skip)
            .Take(req.Take)
            .ToListAsync(ct);

        // Return paginated response
        await SendAsync(new PaginatedResponse<Role>
        {
            Items = roles,
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = totalCount
        }, cancellation: ct);
    }
}
