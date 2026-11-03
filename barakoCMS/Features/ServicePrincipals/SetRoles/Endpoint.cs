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

namespace barakoCMS.Features.ServicePrincipals.SetRoles;

internal sealed class SetPrincipalRolesRequest
{
    public Guid Id { get; set; }
    public List<Guid>? RoleIds { get; set; }
}

/// <summary>PUT /api/service-principals/{id}/roles replaces the principal's roles.</summary>
internal class SetServicePrincipalRolesEndpoint(IDocumentSession session, ICallerCapabilities callers)
    : Endpoint<SetPrincipalRolesRequest, ServicePrincipalResponse>
{
    public override void Configure()
    {
        Put("/api/service-principals/{id}/roles");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(SetPrincipalRolesRequest req, CancellationToken ct)
    {
        var tenant = PrincipalRules.TenantOf(User);
        var principal = await PrincipalRules.InTenantAsync(session, req.Id, tenant, ct);
        if (principal is null) { await Send.NotFoundAsync(ct); return; }

        var roleIds = (req.RoleIds ?? []).Distinct().ToList();
        if (await PrincipalRules.CheckAsync(session, callers, User, roleIds, tenant, ct) is { } why)
            ThrowError(r => r.RoleIds!, why, 403);

        var before = principal.RoleIds.Select(r => r.ToString()).ToList();
        principal.RoleIds = roleIds;
        session.Store(principal);
        await AuditLog.RecordAsync(session, tenant, "service_principal.roles_changed", PrincipalRules.ActorOf(User),
            User.FindFirst("Username")?.Value, targetType: "ServicePrincipal", targetId: principal.Id.ToString(),
            metadata: new() { ["name"] = principal.Name, ["from"] = before, ["to"] = roleIds.Select(r => r.ToString()).ToList() }, ct: ct);
        if (!await PrincipalRules.SaveAsync(session, this, ct)) return;

        await Send.OkAsync(ServicePrincipalResponse.Of(principal), ct);
    }
}
