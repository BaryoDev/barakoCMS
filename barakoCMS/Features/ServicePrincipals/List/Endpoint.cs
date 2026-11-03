using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Features.Roles;
using barakoCMS.Features.Tenants.Members;
using barakoCMS.Features.Users;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using barakoCMS.Features.ServicePrincipals;

namespace barakoCMS.Features.ServicePrincipals.List;

/// <summary>GET /api/service-principals</summary>
internal class ListServicePrincipalsEndpoint(IQuerySession session)
    : Endpoint<ListRequest, PaginatedResponse<ServicePrincipalResponse>>
{
    public override void Configure()
    {
        Get("/api/service-principals");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var tenant = PrincipalRules.TenantOf(User);
        var page = await session.Query<ServicePrincipal>()
            .Where(p => p.TenantSlug == tenant)
            .OrderByDescending(p => p.CreatedAt)
            .ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<ServicePrincipalResponse>
        {
            Items = page.Items.Select(ServicePrincipalResponse.Of).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
