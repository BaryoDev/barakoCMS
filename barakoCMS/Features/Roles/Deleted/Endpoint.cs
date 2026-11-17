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

        var page = await query.OrderBy(r => r.Name).ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(page.Map(barakoCMS.Features.Roles.RoleResponse.From), cancellation: ct);
    }
}
