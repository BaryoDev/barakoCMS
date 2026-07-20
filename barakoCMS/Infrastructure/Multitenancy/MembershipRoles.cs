using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Resolves the roles a user holds in a given tenant: their global <see cref="User.RoleIds"/> unioned
/// with their <see cref="Membership"/> roles in that tenant. Global roles are platform-wide (a
/// SuperAdmin stays a SuperAdmin inside every tenant, so platform-global screens like Users and Roles
/// keep working after switching into a club), while membership roles add tenant-specific access. With
/// no membership this is just the global roles, so single-tenant deployments are unchanged.
/// </summary>
public static class MembershipRoles
{
    public static async Task<List<Guid>> EffectiveRoleIdsAsync(
        IQuerySession session, User user, string tenantSlug, CancellationToken ct)
    {
        var global = user.RoleIds ?? new List<Guid>();

        var membership = await session.Query<Membership>()
            .Where(m => m.UserId == user.Id && m.TenantSlug == tenantSlug && m.Status == MembershipStatus.Active)
            .FirstOrDefaultAsync(ct);

        if (membership is null)
            return global;

        return membership.RoleIds.Union(global).ToList();
    }
}
