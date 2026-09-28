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

        return roles.Any(r => SystemCapabilities.Satisfies(r.SystemCapabilities, SystemCapabilities.ManageUserMembership)
                              || (legacy && legacyRoles.Contains(r.Name)));
    }

    public static async Task<bool> IsSuperAdminAsync(IQuerySession session, ClaimsPrincipal principal, CancellationToken ct) =>
        (await CallerAsync(session, principal, ct))?.RoleIds.Contains(SystemRoles.SuperAdminRoleId) == true;

    private static async Task<User?> CallerAsync(IQuerySession session, ClaimsPrincipal principal, CancellationToken ct) =>
        Guid.TryParse(principal.FindFirst("UserId")?.Value, out var id)
            ? await session.LoadAsync<User>(id, ct)
            : null;
}
