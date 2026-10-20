using Marten;
using barakoCMS.Models;

namespace barakoCMS.Modules;

/// <summary>Finds a seeded role by name among the roles that belong to the deployment.</summary>
/// <remarks>
/// A role name is unique only within one owner, so a tenant's administrator can make a role named
/// after a module's seeded role. A lookup by name reads only ownerless and platform roles, and then
/// the default tenant's, which is where every role an operator made before 5.0 went. Another
/// tenant's role is never picked.
/// </remarks>
internal static class DeploymentRoleNames
{
    public static bool Matches(Role role, string name) =>
        role.DeletedAt is null && role.Name == name && (role.TenantSlug is null || role.TenantSlug == Tenant.DefaultSlug);

    public static async Task<Role?> FindAsync(IQuerySession session, string name, CancellationToken ct)
    {
        var found = await session.Query<Role>()
            .Where(r => r.DeletedAt == null && r.Name == name && (r.TenantSlug == null || r.TenantSlug == Tenant.DefaultSlug))
            .ToListAsync(ct);
        return found.OrderBy(r => r.TenantSlug is null ? 0 : 1).FirstOrDefault();
    }
}
