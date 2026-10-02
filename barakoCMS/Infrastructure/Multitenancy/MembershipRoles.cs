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
        (await ResolveAsync(session, user, tenantSlug, ct)).RoleIds;

    /// <summary>
    /// The one body behind <see cref="EffectiveRoleIdsAsync"/>: the effective role ids, and the
    /// active membership they were read from so its profile costs no second query.
    /// </summary>
    /// <remarks>
    /// Anything that changes which roles a user holds in a tenant goes here, so the permission
    /// resolver (which needs the row) and every caller of the wrapper cannot come to differ.
    /// </remarks>
    internal static async Task<(List<Guid> RoleIds, Membership? Membership)> ResolveAsync(
        IQuerySession session, User user, string tenantSlug, CancellationToken ct)
    {
        var global = user.RoleIds ?? new List<Guid>();

        var membership = await session.Query<Membership>()
            .Where(m => m.UserId == user.Id && m.TenantSlug == tenantSlug && m.Status == MembershipStatus.Active)
            .FirstOrDefaultAsync(ct);

        if (membership is null)
            return (global, null);

        return (membership.RoleIds.Union(global).ToList(), membership);
    }
}
