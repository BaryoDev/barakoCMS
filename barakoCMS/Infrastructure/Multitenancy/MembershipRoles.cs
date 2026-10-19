using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Resolves the roles a user holds in a given tenant: their global <see cref="User.RoleIds"/> unioned
/// with their <see cref="Membership"/> roles in that tenant, less any role another tenant owns.
/// Global roles are platform-wide (a SuperAdmin stays a SuperAdmin inside every tenant, so
/// platform-global screens like Users and Roles keep working after switching into a club), while
/// membership roles add tenant-specific access. With no membership this is just the global roles,
/// so single-tenant deployments are unchanged.
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
        var global = (user.RoleIds ?? new List<Guid>())
            .Union(await GroupRoleIdsAsync(session, user, ct))
            .ToList();

        var membership = await session.Query<Membership>()
            .Where(m => m.UserId == user.Id && m.TenantSlug == tenantSlug && m.Status == MembershipStatus.Active)
            .FirstOrDefaultAsync(ct);

        var held = membership?.RoleIds ?? [];
        var foreign = await ForeignAsync(session, global.Concat(held).Distinct().ToList(), tenantSlug, ct);

        // A global role grants in every tenant, so only a platform role may be one. A tenant's role
        // held globally, which no write path allows any more, grants nothing outside that tenant.
        if (foreign.Count > 0)
        {
            global = global.Except(foreign).ToList();
        }

        if (membership is null)
        {
            return (global, null);
        }

        // A membership holds only its own tenant's roles and the platform roles. One that names
        // another tenant's role, which no write path allows any more, grants nothing from it here.
        if (foreign.Count > 0)
        {
            held = held.Except(foreign).ToList();
        }

        return (held.Union(global).ToList(), membership);
    }

    /// <summary>The roles of every group the user is a member of, on either side of the link.</summary>
    /// <remarks>
    /// A member is a user whose <see cref="User.GroupIds"/> names the group or whom the group's
    /// <see cref="UserGroup.UserIds"/> lists, because the two endpoints that add a member write
    /// different sides. Nested groups are not followed: a child group does not inherit its parent's
    /// roles. These count as global roles, so a tenant's role placed on a group grants nothing.
    /// </remarks>
    private static async Task<List<Guid>> GroupRoleIdsAsync(IQuerySession session, User user, CancellationToken ct)
    {
        var groupIds = user.GroupIds ?? [];
        var groups = await session.Query<UserGroup>()
            .Where(g => g.UserIds.Contains(user.Id) || groupIds.Contains(g.Id))
            .ToListAsync(ct);
        return groups.SelectMany(g => g.RoleIds ?? []).Distinct().ToList();
    }

    /// <summary>Which of <paramref name="roleIds"/> belong to a tenant other than <paramref name="tenantSlug"/>.</summary>
    /// <remarks>
    /// A role with no owner at all is one stored before roles had owners, which role-tenancy.sql
    /// gives one; it is left as it always was.
    /// </remarks>
    private static async Task<List<Guid>> ForeignAsync(
        IQuerySession session, List<Guid> roleIds, string tenantSlug, CancellationToken ct)
    {
        if (roleIds.Count == 0)
        {
            return [];
        }

        var foreign = await session.Query<Role>()
            .Where(r => roleIds.Contains(r.Id) && !r.IsPlatform && r.TenantSlug != null && r.TenantSlug != tenantSlug)
            .Select(r => r.Id)
            .ToListAsync(ct);
        return foreign.ToList();
    }
}
