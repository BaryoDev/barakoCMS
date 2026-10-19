using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using Marten;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Features.Roles;

/// <summary>Which roles a caller reaches: their tenant's own and the platform roles every tenant shares.</summary>
/// <remarks>
/// A role stored before roles had owners, with neither a tenant nor the platform flag, counts as the
/// default tenant's, which is where migrations/5.0.0/role-tenancy.sql puts a role nobody holds.
/// A role outside the caller's reach answers as if it did not exist, so a tenant cannot learn the
/// ids of another's roles.
/// </remarks>
internal static class RoleScope
{
    /// <summary>The gate for platform roles: changing them, and seeing every tenant's roles.</summary>
    public static readonly RequiredCapability Platform = new(SystemCapabilities.ManagePlatformRoles, ["SuperAdmin"]);

    public const string PlatformRoleMessage =
        "This is a platform role every tenant shares, so changing it needs the 'manage_platform_roles' capability.";

    public const string PlatformCapabilityMessage =
        "A tenant's role cannot carry a capability that reaches past the tenant. Only a platform administrator can grant one.";

    public static Task<bool> IsPlatformCallerAsync(HttpContext http, CancellationToken ct) =>
        CapabilityGateProcessor.HoldsAsync(http, Platform, ct);

    /// <summary>
    /// The same gate for a config document's caller. The host applying its own seed file passes:
    /// it is the deployment, not a tenant's administrator.
    /// </summary>
    public static async Task<bool> IsPlatformCallerAsync(
        barakoCMS.Core.Interfaces.ConfigCaller caller, barakoCMS.Core.Interfaces.ICallerCapabilities callers,
        IConfiguration configuration, CancellationToken ct)
    {
        if (caller.Principal is not { } principal)
        {
            return true;
        }

        if (Platform.LegacyRoles.Any(principal.IsInRole)
            && configuration.GetValue(CapabilityGateProcessor.LegacyRoleFallbackKey, false))
        {
            return true;
        }

        return await callers.HoldsAsync(principal, Platform.Capability, ct);
    }

    /// <summary>The tenant a role belongs to, or null for a platform role.</summary>
    public static string? OwnerOf(Role role) =>
        role.IsPlatform ? null : string.IsNullOrEmpty(role.TenantSlug) ? Tenant.DefaultSlug : role.TenantSlug;

    public static bool Reaches(Role role, string tenantSlug) =>
        role.IsPlatform || string.Equals(OwnerOf(role), tenantSlug, StringComparison.Ordinal);

    /// <summary>The live roles <paramref name="tenantSlug"/> reaches, as a query.</summary>
    public static IQueryable<Role> In(IQueryable<Role> roles, string tenantSlug) =>
        Reach(roles.Where(r => r.DeletedAt == null), tenantSlug);

    /// <summary>The roles <paramref name="tenantSlug"/> reaches, deleted or not.</summary>
    public static IQueryable<Role> Reach(IQueryable<Role> roles, string tenantSlug) =>
        tenantSlug == Tenant.DefaultSlug
            ? roles.Where(r => r.IsPlatform || r.TenantSlug == tenantSlug || r.TenantSlug == null)
            : roles.Where(r => r.IsPlatform || r.TenantSlug == tenantSlug);

    /// <summary>
    /// The roles <paramref name="tenantSlug"/> reaches, by name. The tenant's own role comes ahead of
    /// a platform role of the same name, since names are unique only within one owner.
    /// </summary>
    public static async Task<Dictionary<string, Role>> ByNameAsync(IQuerySession session, string tenantSlug, CancellationToken ct)
    {
        var byName = new Dictionary<string, Role>(StringComparer.Ordinal);
        foreach (var role in (await In(session.Query<Role>(), tenantSlug).ToListAsync(ct)).OrderBy(r => r.IsPlatform))
        {
            byName.TryAdd(role.Name, role);
        }

        return byName;
    }

    /// <summary>Whether every id names a role <paramref name="tenantSlug"/> reaches.</summary>
    public static async Task<bool> AllReachableAsync(
        IQuerySession session, IReadOnlyCollection<Guid> roleIds, string tenantSlug, CancellationToken ct)
    {
        var ids = roleIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return true;
        }

        var found = await session.Query<Role>().Where(r => r.DeletedAt == null && ids.Contains(r.Id)).ToListAsync(ct);
        return found.Count == ids.Count && found.All(r => Reaches(r, tenantSlug));
    }

    /// <summary>Whether another live role in the same owner already has <paramref name="name"/>.</summary>
    public static async Task<bool> NameTakenAsync(
        IQuerySession session, string name, string? ownerSlug, Guid? except, CancellationToken ct)
    {
        var sameName = await session.Query<Role>().Where(r => r.Name == name && r.DeletedAt == null).ToListAsync(ct);
        return sameName.Any(r => r.Id != except && OwnerOf(r) == ownerSlug);
    }

    /// <summary>
    /// Erases the deleted roles in the same owner that still hold <paramref name="name"/>, so a new
    /// role can take it, as it always could after a delete. Only queues the deletes: the caller
    /// stores the new or renamed role and saves once, so the erase and the take commit together.
    /// Relies on Marten keeping queue order, so call this before storing the role.
    /// </summary>
    public static async Task ReleaseDeletedNameAsync(
        IDocumentSession session, string name, string? ownerSlug, string tenantSlug,
        Guid? actorId, string? actorName, CancellationToken ct)
    {
        var deleted = (await session.Query<Role>().Where(r => r.Name == name && r.DeletedAt != null).ToListAsync(ct))
            .Where(r => OwnerOf(r) == ownerSlug)
            .ToList();
        if (deleted.Count == 0)
        {
            return;
        }

        foreach (var role in deleted)
        {
            session.Delete(role);
            await barakoCMS.Infrastructure.Audit.AuditLog.RecordAsync(session, tenantSlug, "role.erased", actorId, actorName,
                targetType: "Role", targetId: role.Id.ToString(),
                metadata: new Dictionary<string, object> { ["name"] = role.Name, ["reason"] = "name reused" }, ct: ct);
        }
    }
}
