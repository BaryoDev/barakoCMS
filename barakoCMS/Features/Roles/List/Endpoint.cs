using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.List;

internal class Request : PaginatedRequest
{
}

/// <summary>GET /api/roles: the caller's tenant's roles and the platform roles, or every role for a platform caller.</summary>
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, PaginatedResponse<barakoCMS.Features.Roles.RoleResponse>>
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
        var query = session.Query<Role>().Where(r => r.DeletedAt == null);
        if (!await RoleScope.IsPlatformCallerAsync(HttpContext, ct))
        {
            query = RoleScope.In(query, tenant.Slug);
        }

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
