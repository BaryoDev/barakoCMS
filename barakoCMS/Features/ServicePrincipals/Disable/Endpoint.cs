using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Features.Roles;
using barakoCMS.Features.Tenants.Members;
using barakoCMS.Features.Users;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Services;
using FastEndpoints;
using Marten;
using barakoCMS.Features.ServicePrincipals;

namespace barakoCMS.Features.ServicePrincipals.Disable;

/// <summary>POST /api/service-principals/{id}/disable. Its keys are refused on their next request.</summary>
internal class DisableServicePrincipalEndpoint(IDocumentSession session, ICallerCapabilities callers, IPermissionResolver permissions) : EndpointWithoutRequest<ServicePrincipalResponse>
{
    public override void Configure()
    {
        Post("/api/service-principals/{id}/disable");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var tenant = PrincipalRules.TenantOf(User);
        var principal = await PrincipalRules.InTenantAsync(session, Route<Guid>("id"), tenant, ct);
        if (principal is null) { await Send.NotFoundAsync(ct); return; }

        // Disabling a principal stops what its roles do, so a caller who lacks what it holds cannot.
        if (await PrincipalRules.NotHeldByPrincipalAsync(session, callers, permissions, User, principal, ct) is { } notHeld)
            ThrowError(notHeld, 403);

        // Disabling twice writes nothing, so the trail holds one row per disable.
        if (!principal.Disabled)
        {
            principal.DisabledAt = DateTime.UtcNow;
            session.Store(principal);
            await AuditLog.RecordAsync(session, tenant, "service_principal.disabled", PrincipalRules.ActorOf(User),
                User.FindFirst("Username")?.Value, targetType: "ServicePrincipal", targetId: principal.Id.ToString(),
                metadata: new() { ["name"] = principal.Name }, ct: ct);
            if (!await PrincipalRules.SaveAsync(session, this, ct)) return;
        }

        await Send.OkAsync(ServicePrincipalResponse.Of(principal), ct);
    }
}
