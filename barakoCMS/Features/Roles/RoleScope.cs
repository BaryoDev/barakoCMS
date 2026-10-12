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

    /// <summary>The tenant a role belongs to, or null for a platform role.</summary>
    public static string? OwnerOf(Role role) =>
        role.IsPlatform ? null : string.IsNullOrEmpty(role.TenantSlug) ? Tenant.DefaultSlug : role.TenantSlug;

    public static bool Reaches(Role role, string tenantSlug) =>
        role.IsPlatform || string.Equals(OwnerOf(role), tenantSlug, StringComparison.Ordinal);

    /// <summary>The roles <paramref name="tenantSlug"/> reaches, as a query.</summary>
    public static IQueryable<Role> In(IQueryable<Role> roles, string tenantSlug) =>
        tenantSlug == Tenant.DefaultSlug
            ? roles.Where(r => r.IsPlatform || r.TenantSlug == tenantSlug || r.TenantSlug == null)
            : roles.Where(r => r.IsPlatform || r.TenantSlug == tenantSlug);

    /// <summary>Whether every id names a role <paramref name="tenantSlug"/> reaches.</summary>
    public static async Task<bool> AllReachableAsync(
        IQuerySession session, IReadOnlyCollection<Guid> roleIds, string tenantSlug, CancellationToken ct)
    {
        var ids = roleIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return true;
        }

        var found = await session.Query<Role>().Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        return found.Count == ids.Count && found.All(r => Reaches(r, tenantSlug));
    }

    /// <summary>Whether another role in the same owner already has <paramref name="name"/>.</summary>
    public static async Task<bool> NameTakenAsync(
        IQuerySession session, string name, string? ownerSlug, Guid? except, CancellationToken ct)
    {
        var sameName = await session.Query<Role>().Where(r => r.Name == name).ToListAsync(ct);
        return sameName.Any(r => r.Id != except && OwnerOf(r) == ownerSlug);
    }
}
