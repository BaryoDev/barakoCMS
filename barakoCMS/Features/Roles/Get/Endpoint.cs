using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Get;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, barakoCMS.Features.Roles.RoleResponse>
{
    public override void Configure()
    {
        Get("/api/roles/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageRoles, "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var role = await session.LoadAsync<Role>(req.Id, ct);

        if (role == null
            || (!RoleScope.Reaches(role, tenant.Slug) && !await RoleScope.IsPlatformCallerAsync(HttpContext, ct)))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(barakoCMS.Features.Roles.RoleResponse.From(role), ct);
    }
}
