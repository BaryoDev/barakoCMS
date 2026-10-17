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

        var role = await session.LoadAsync<Role>(req.Id, ct);
        var platformCaller = await RoleScope.IsPlatformCallerAsync(HttpContext, ct);
        if (role is not null && !RoleScope.Reaches(role, tenant.Slug) && !platformCaller)
            role = null;

        // Here and not in a validator: a condition the stored role already holds is passed over,
        // and that takes the stored role.
        foreach (var error in await ReferenceConditionRules.CheckAsync(session, req.Permissions, role?.Permissions, ct))
            AddError(r => r.Permissions, error);

        // A set the request leaves out is kept, so a console that does not know field sets does not
        // drop them by saving the role. An empty list is what removes one.
        FieldSetRules.CarryOver(req.Permissions, role?.Permissions);
        FieldSetRules.Normalise(req.Permissions);
        foreach (var error in await FieldSetRules.CheckAsync(session, req.Permissions, role?.Permissions, ct))
            AddError(r => r.Permissions, error);

        if (role is not null && (req.Invitable ?? role.Invitable)
            && !InvitableRule.Allows(role.Id, role.IsPlatform, req.SystemCapabilities))
            AddError(r => r.Invitable, InvitableRule.Message);

        ThrowIfAnyErrors();

        if (role == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await RefuseOutOfReachAsync(req, role, platformCaller, ct);

        var before = RoleAudit.Of(role);

        role.Name = req.Name;
        role.Description = req.Description;
        role.Permissions = req.Permissions;
        role.SystemCapabilities = req.SystemCapabilities;
        if (req.RequireMfa is { } requireMfa)
            role.RequireMfa = requireMfa;
        if (req.Invitable is { } invitable)
            role.Invitable = invitable;

        session.Store(role);

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "role.updated", actorId, User.FindFirst("Username")?.Value,
            targetType: "Role", targetId: role.Id.ToString(),
            metadata: RoleAudit.Changed(before, RoleAudit.Of(role)), ct: ct);
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

    /// <summary>
    /// Throws for an edit past the caller's reach: a reserved name, a platform role or a platform
    /// capability without manage_platform_roles, or a name another role of the same owner has.
    /// </summary>
    private async Task RefuseOutOfReachAsync(Request req, Role role, bool platformCaller, CancellationToken ct)
    {
        // A seeded role keeps its own name; anything else taking a reserved one is the escalation
        // path SystemRoles.Reserved describes, so it is refused after the load, when the id is known.
        if (SystemRoles.IsReservedName(req.Name) && !SystemRoles.Contains(role.Id))
        {
            AddError(r => r.Name, SystemRoles.ReservedNameMessage(req.Name));
            ThrowIfAnyErrors();
        }

        if (role.IsPlatform && !platformCaller)
        {
            ThrowError(RoleScope.PlatformRoleMessage, 403);
        }

        // Only capabilities being added count, the rule a member's roles follow: one a platform
        // administrator gave this role is kept on an unrelated edit.
        if (!platformCaller && req.SystemCapabilities
                .Except(role.SystemCapabilities ?? [], StringComparer.OrdinalIgnoreCase)
                .Any(c => barakoCMS.Features.Users.PlatformRoles.PlatformCapabilities.Contains(c)))
        {
            ThrowError(RoleScope.PlatformCapabilityMessage, 403);
        }

        if (!string.Equals(role.Name, req.Name, StringComparison.Ordinal)
            && await RoleScope.NameTakenAsync(session, req.Name, RoleScope.OwnerOf(role), role.Id, ct))
        {
            ThrowError("Another role here already has this name.", 409);
        }
    }
}
