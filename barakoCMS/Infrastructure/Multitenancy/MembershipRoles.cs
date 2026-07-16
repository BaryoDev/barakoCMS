using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Resolves the roles a user holds in a given tenant. Prefers the user's <see cref="Membership"/> in
/// that tenant; falls back to the legacy <see cref="User.RoleIds"/> when there is no membership, so
/// single-tenant deployments keep working during and after the move to membership-based roles.
/// </summary>
public static class MembershipRoles
{
    public static async Task<List<Guid>> EffectiveRoleIdsAsync(
        IQuerySession session, User user, string tenantSlug, CancellationToken ct)
    {
        var membership = await session.Query<Membership>()
            .Where(m => m.UserId == user.Id && m.TenantSlug == tenantSlug && m.Status == MembershipStatus.Active)
            .FirstOrDefaultAsync(ct);

        if (membership != null)
            return membership.RoleIds;

        return user.RoleIds ?? new List<Guid>();
    }
}
