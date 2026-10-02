using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Update;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    CapabilityVocabulary vocabulary,
    IConfiguration configuration,
    ILogger<Endpoint> logger,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/roles/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageRoles, "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var unknown = vocabulary.Unknown(req.SystemCapabilities);
        if (configuration.GetValue(CapabilityVocabulary.RefuseUnknownKey, false))
        {
            foreach (var name in unknown)
                AddError(r => r.SystemCapabilities, CapabilityVocabulary.UnknownMessage(name));
        }
        ThrowIfAnyErrors();

        var role = await session.LoadAsync<Role>(req.Id, ct);

        if (role == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // A seeded role keeps its own name; anything else taking a reserved one is the escalation
        // path SystemRoles.Reserved describes, so it is refused after the load, when the id is known.
        if (SystemRoles.IsReservedName(req.Name) && !SystemRoles.Contains(role.Id))
        {
            AddError(r => r.Name, SystemRoles.ReservedNameMessage(req.Name));
            ThrowIfAnyErrors();
        }

        var nameBefore = role.Name;
        var capabilitiesBefore = RoleAudit.Capabilities(role);
        var permissionsBefore = RoleAudit.Permissions(role.Permissions);

        role.Name = req.Name;
        role.Description = req.Description;
        role.Permissions = req.Permissions;
        role.SystemCapabilities = req.SystemCapabilities;

        session.Store(role);

        var capabilitiesAfter = RoleAudit.Capabilities(role);
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "role.updated", actorId, User.FindFirst("Username")?.Value,
            targetType: "Role", targetId: role.Id.ToString(),
            metadata: new()
            {
                ["name"] = role.Name,
                ["nameBefore"] = nameBefore,
                ["capabilitiesBefore"] = capabilitiesBefore,
                ["capabilitiesAfter"] = capabilitiesAfter,
                ["capabilitiesAdded"] = RoleAudit.Added(capabilitiesBefore, capabilitiesAfter),
                ["capabilitiesRemoved"] = RoleAudit.Added(capabilitiesAfter, capabilitiesBefore),
                ["permissionsBefore"] = permissionsBefore,
                ["permissionsAfter"] = RoleAudit.Permissions(role.Permissions),
            },
            ct: ct);
        await session.SaveChangesAsync(ct);

        // Permissions changed, so evict cached decisions and the new rules take effect immediately.
        permissionResolver.InvalidateAllPermissions();

        if (unknown.Count > 0)
        {
            logger.LogWarning(
                "Role {RoleName} ({RoleId}) holds capabilities this instance does not know: {UnknownCapabilities}",
                role.Name, role.Id, string.Join(", ", unknown));
        }

        await Send.OkAsync(new Response
        {
            Message = "Role updated successfully",
            UnknownCapabilities = unknown.ToList(),
        }, ct);
    }
}
