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

        query = req.SortOrder.ToLower() == "asc"
            ? query.OrderBy(r => r.Name)
            : query.OrderByDescending(r => r.Name);

        var page = await query.ToPagedResponseAsync(req, ct);

        var counts = await RoleHolderCounts.ForAsync(session, tenant.Slug, page.Items, ct);

        await Send.ResponseAsync(page.Map(r => barakoCMS.Features.Roles.RoleResponse.WithHolders(
            r, counts.GetValueOrDefault(r.Id))), cancellation: ct);
    }
}
