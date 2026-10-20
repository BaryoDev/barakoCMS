using System.Security.Claims;
using barakoCMS.Features.Users;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.UserGroups;

/// <summary>
/// Who may change <see cref="UserGroup.RoleIds"/>. A group's roles reach every member in every
/// tenant, so this holds the same line as assigning a global role to a user: a platform
/// administrator only, platform-capability roles by a SuperAdmin only, and no tenant-owned roles.
/// Without it an Admin who manages groups would grant itself any role through one.
/// </summary>
internal static class GroupRoleGuard
{
    private static readonly IReadOnlyList<string> LegacyRoles = barakoCMS.Infrastructure.Auth.CapabilityGate.AdminLegacyRoles;

    /// <summary>A refusal as a status and message, or null when the change is allowed.</summary>
    public static async Task<(int Status, string Message)?> CheckAsync(
        IQuerySession session, ClaimsPrincipal caller, IConfiguration configuration,
        IReadOnlyCollection<Guid> before, IReadOnlyCollection<Guid> after, CancellationToken ct)
    {
        var added = after.Except(before).ToList();
        var removed = before.Except(after).ToList();
        if (added.Count == 0 && removed.Count == 0) return null;

        if (!await PlatformRoles.MayChangeAsync(session, caller, configuration, LegacyRoles, ct))
            return (403, PlatformRoles.RefusedMessage);

        var touched = added.Concat(removed).ToList();
        var roles = await session.Query<Role>().Where(r => touched.Contains(r.Id)).ToListAsync(ct);

        var missing = added.Except(roles.Where(r => r.DeletedAt == null).Select(r => r.Id)).ToList();
        if (missing.Count > 0)
            return (400, $"Role not found: {string.Join(", ", missing)}.");

        if (roles.Any(r => added.Contains(r.Id) && !r.IsPlatform && !string.IsNullOrEmpty(r.TenantSlug)))
            return (400, "A group holds platform roles only. A tenant's role is given through that tenant's members routes.");

        if (roles.Any(PlatformRoles.CarriesPlatformCapability) && !await PlatformRoles.IsSuperAdminAsync(session, caller, ct))
            return (403, PlatformRoles.PlatformRoleRefusedMessage);

        return null;
    }
}
