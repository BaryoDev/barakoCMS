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

namespace barakoCMS.Features.ServicePrincipals.Create;

internal sealed class CreateServicePrincipalRequest
{
    public string Name { get; set; } = string.Empty;
    public List<Guid>? RoleIds { get; set; }
}

/// <summary>POST /api/service-principals</summary>
internal class CreateServicePrincipalEndpoint(IDocumentSession session, ICallerCapabilities callers)
    : Endpoint<CreateServicePrincipalRequest, ServicePrincipalResponse>
{
    public override void Configure()
    {
        Post("/api/service-principals");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CreateServicePrincipalRequest req, CancellationToken ct)
    {
        var tenant = PrincipalRules.TenantOf(User);
        if (PrincipalRules.NameProblem(req.Name, out var name) is { } nameProblem)
            ThrowError(r => r.Name, nameProblem);

        var roleIds = (req.RoleIds ?? []).Distinct().ToList();
        if (await PrincipalRules.CheckAsync(session, callers, User, roleIds, tenant, ct) is { } why)
            ThrowError(r => r.RoleIds!, why, 403);

        var actor = PrincipalRules.ActorOf(User);
        var principal = new ServicePrincipal
        {
            Name = name,
            TenantSlug = tenant,
            RoleIds = roleIds,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = actor ?? Guid.Empty,
        };
        session.Store(principal);
        await AuditLog.RecordAsync(session, tenant, "service_principal.created", actor, User.FindFirst("Username")?.Value,
            targetType: "ServicePrincipal", targetId: principal.Id.ToString(),
            metadata: new() { ["name"] = principal.Name, ["roleIds"] = roleIds.Select(r => r.ToString()).ToList() }, ct: ct);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(ServicePrincipalResponse.Of(principal), ct);
    }
}
