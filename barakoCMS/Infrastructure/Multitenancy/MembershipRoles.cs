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
        IQuerySession session, User user, string tenantSlug, CancellationToken ct) =>
        EffectiveRoleIds(user, await ActiveMembershipAsync(session, user.Id, tenantSlug, ct));

    /// <summary>
    /// The user's active membership in the tenant, or null. One query, so a caller that needs the
    /// member profile as well as the roles reads both from the same row.
    /// </summary>
    public static Task<Membership?> ActiveMembershipAsync(
        IQuerySession session, Guid userId, string tenantSlug, CancellationToken ct) =>
        session.Query<Membership>()
            .Where(m => m.UserId == userId && m.TenantSlug == tenantSlug && m.Status == MembershipStatus.Active)
            .FirstOrDefaultAsync(ct);

    public static List<Guid> EffectiveRoleIds(User user, Membership? membership)
    {
        var global = user.RoleIds ?? new List<Guid>();

        if (membership is null)
            return global;

        return membership.RoleIds.Union(global).ToList();
    }
}
