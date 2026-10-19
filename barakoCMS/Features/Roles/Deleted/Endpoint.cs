using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Deleted;

internal class Request : PaginatedRequest
{
}

/// <summary>GET /api/roles/deleted: the roles that were deleted and not yet erased, within the caller's reach.</summary>
internal class Endpoint(
    IQuerySession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, PaginatedResponse<barakoCMS.Features.Roles.RoleResponse>>
{
    public override void Configure()
    {
        Get("/api/roles/deleted");
        Definition.RequireCapability(barakoCMS.Features.Roles.List.Endpoint.Gate.Capability, [.. barakoCMS.Features.Roles.List.Endpoint.Gate.LegacyRoles]);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var query = session.Query<Role>().Where(r => r.DeletedAt != null);
        if (!await RoleScope.IsPlatformCallerAsync(HttpContext, ct))
        {
            query = RoleScope.Reach(query, tenant.Slug);
        }

        var total = await query.CountAsync(ct);
        var roles = await query.OrderBy(r => r.Name).Skip(req.Skip).Take(req.Take).ToListAsync(ct);

        await Send.ResponseAsync(new PaginatedResponse<barakoCMS.Features.Roles.RoleResponse>
        {
            Items = roles.Select(barakoCMS.Features.Roles.RoleResponse.From).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = total
        }, cancellation: ct);
    }
}
