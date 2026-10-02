using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.List;

internal class Request : PaginatedRequest
{
}

internal class Endpoint(
    IDocumentSession session) : Endpoint<Request, PaginatedResponse<barakoCMS.Features.Roles.RoleResponse>>
{
    /// <summary>
    /// Who may read a role's capabilities and permissions. The audit list asks this same gate
    /// before it returns that detail of a role entry.
    /// </summary>
    internal static readonly RequiredCapability Gate = new(SystemCapabilities.ManageRoles, ["SuperAdmin"]);

    public override void Configure()
    {
        Get("/api/roles");
        Definition.RequireCapability(Gate.Capability, [.. Gate.LegacyRoles]);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var query = session.Query<Role>().AsQueryable();

        var totalCount = await query.CountAsync(ct);

        query = req.SortOrder.ToLower() == "asc"
            ? query.OrderBy(r => r.Name)
            : query.OrderByDescending(r => r.Name);

        var roles = await query
            .Skip(req.Skip)
            .Take(req.Take)
            .ToListAsync(ct);

        await Send.ResponseAsync(new PaginatedResponse<barakoCMS.Features.Roles.RoleResponse>
        {
            Items = roles.Select(barakoCMS.Features.Roles.RoleResponse.From).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = totalCount
        }, cancellation: ct);
    }
}
