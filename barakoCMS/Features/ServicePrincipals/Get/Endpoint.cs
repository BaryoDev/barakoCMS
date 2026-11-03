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

namespace barakoCMS.Features.ServicePrincipals.Get;

/// <summary>GET /api/service-principals/{id}</summary>
internal class GetServicePrincipalEndpoint(IQuerySession session) : EndpointWithoutRequest<ServicePrincipalResponse>
{
    public override void Configure()
    {
        Get("/api/service-principals/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var principal = await PrincipalRules.InTenantAsync(session, Route<Guid>("id"), PrincipalRules.TenantOf(User), ct);
        if (principal is null) { await Send.NotFoundAsync(ct); return; }
        await Send.OkAsync(ServicePrincipalResponse.Of(principal), ct);
    }
}
