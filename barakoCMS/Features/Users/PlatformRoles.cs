using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using System.Security.Claims;
using Marten;

namespace barakoCMS.Features.Users;

/// <summary>
/// Who may change <see cref="User.RoleIds"/>, the roles a user holds in every tenant.
/// </summary>
/// <remarks>
/// The capability gate answers from the caller's effective roles in the current tenant, which
/// include their membership roles there. That is right for a tenant screen and wrong for these
/// routes: an Admin of one tenant holds manage_user_membership through the membership, and a role
/// written to <see cref="User.RoleIds"/> applies in every tenant. So the caller's own capability has
/// to come from a role in their <see cref="User.RoleIds"/> too. A tenant administrator changes roles
/// inside their tenant through <c>/api/tenants/members</c>.
/// </remarks>
internal static class PlatformRoles
{
    public const string RefusedMessage =
        "Global roles can only be changed by a platform administrator. Use tenant membership to give roles in this tenant.";

    public const string PlatformRoleRefusedMessage =
        "This role carries a platform capability, so only a platform administrator (SuperAdmin) can grant it.";

    public const string PlatformRoleRemovalRefusedMessage =
        "This role carries a platform capability, so only a platform administrator (SuperAdmin) can remove it.";

    /// <summary>
    /// SuperAdmin-only capabilities that reach past a tenant: editing role documents, tenants, every
    /// user account, or the deployment's mail settings.
    /// </summary>
    /// <remarks>
    /// A role carrying one is granted only by a SuperAdmin, on either surface, and removed from a
    /// user's global roles only by one. Admin does not hold manage_roles, so a custom role carrying
    /// it would otherwise be how an Admin, or an administrator of one tenant through a membership,
    /// reaches every role in the deployment.
    /// </remarks>
    public static readonly IReadOnlySet<string> PlatformCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        SystemCapabilities.All,
        SystemCapabilities.ManageRoles,
        SystemCapabilities.ManageTenants,
        SystemCapabilities.ManageUsers,
        SystemCapabilities.ManageEmailSettings,
    };

    public static bool CarriesPlatformCapability(Role role) =>
        role.Id == SystemRoles.SuperAdminRoleId || (role.SystemCapabilities ?? []).Any(PlatformCapabilities.Contains);

    /// <summary>
    /// Whether one of the caller's global roles grants manage_user_membership, or, with
    /// <see cref="CapabilityGateProcessor.LegacyRoleFallbackKey"/> on, carries one of the legacy
    /// role names the endpoint's gate still honours.
    /// </summary>
    public static async Task<bool> MayChangeAsync(
        IQuerySession session, ClaimsPrincipal principal, IConfiguration configuration,
        IReadOnlyCollection<string> legacyRoles, CancellationToken ct)
    {
        var caller = await CallerAsync(session, principal, ct);
        if (caller is null || caller.RoleIds.Count == 0) return false;
        if (caller.RoleIds.Contains(SystemRoles.SuperAdminRoleId)) return true;

        var legacy = configuration.GetValue(CapabilityGateProcessor.LegacyRoleFallbackKey, false);
        var ids = caller.RoleIds;
        var roles = await session.Query<Role>().Where(r => ids.Contains(r.Id)).ToListAsync(ct);

        return roles.Any(r => SystemCapabilities.Satisfies(r.SystemCapabilities ?? [], SystemCapabilities.ManageUserMembership)
                              || (legacy && legacyRoles.Contains(r.Name)));
    }

    public static async Task<bool> IsSuperAdminAsync(IQuerySession session, ClaimsPrincipal principal, CancellationToken ct) =>
        (await CallerAsync(session, principal, ct))?.RoleIds.Contains(SystemRoles.SuperAdminRoleId) == true;

    private static async Task<User?> CallerAsync(IQuerySession session, ClaimsPrincipal principal, CancellationToken ct) =>
        Guid.TryParse(principal.FindFirst("UserId")?.Value, out var id)
            ? await session.LoadAsync<User>(id, ct)
            : null;
}
