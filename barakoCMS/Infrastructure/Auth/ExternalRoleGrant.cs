using System.Security.Claims;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Gives a person roles in a tenant because an identity provider's claims said so, for the
/// ExternalAuth module's claim-to-role map. The one place that map reaches a membership.
/// </summary>
/// <remarks>
/// Public because the ExternalAuth module calls it, the same way it calls <see cref="SignUpPolicy"/>.
/// It only adds. A claim that stops being sent takes nothing away, since a provider's claim cannot
/// tell a role it granted from one an administrator gave by hand.
/// </remarks>
public static class ExternalRoleGrant
{
    /// <summary>
    /// The ids in <paramref name="roleIds"/> that a claim may not grant in <paramref name="tenantSlug"/>:
    /// a role that does not exist, one that belongs to another tenant, and one carrying a platform
    /// capability or SuperAdmin, which only a SuperAdmin hands out.
    /// </summary>
    public static async Task<List<Guid>> RefusedAsync(
        IQuerySession session, string tenantSlug, IReadOnlyCollection<Guid> roleIds, CancellationToken ct)
    {
        var ids = roleIds.Distinct().ToList();
        var found = ids.Count == 0
            ? []
            : await session.Query<Role>().Where(r => ids.Contains(r.Id)).ToListAsync(ct);

        return ids.Where(id =>
        {
            var role = found.FirstOrDefault(r => r.Id == id);
            return role is null
                || !barakoCMS.Features.Roles.RoleScope.Reaches(role, tenantSlug)
                || !barakoCMS.Features.Tenants.Members.Members.IsAssignable(role.Id)
                || barakoCMS.Features.Users.PlatformRoles.CarriesPlatformCapability(role);
        }).ToList();
    }

    /// <summary>Stages the roles the person should hold in the tenant. Nothing is saved.</summary>
    /// <returns>
    /// True when a membership was added or widened. False when the tenant is the default one, is
    /// unknown or not Active, the person's membership was removed or suspended (a claim does not
    /// undo that), no role is one a claim may grant, or they already hold every role.
    /// </returns>
    public static async Task<bool> StageAsync(
        IDocumentSession session, User user, string tenantSlug, IReadOnlyCollection<Guid> roleIds,
        string source, CancellationToken ct)
    {
        var slug = string.IsNullOrWhiteSpace(tenantSlug) ? Tenant.DefaultSlug : tenantSlug.Trim().ToLowerInvariant();
        if (slug == Tenant.DefaultSlug || roleIds.Count == 0)
        {
            return false;
        }

        var tenant = await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == slug, ct);
        if (tenant is not { Status: TenantStatus.Active })
        {
            return false;
        }

        var granted = roleIds.Distinct().ToList();
        var refused = await RefusedAsync(session, slug, granted, ct);
        granted = granted.Except(refused).ToList();
        if (granted.Count == 0)
        {
            return false;
        }

        var actor = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("UserId", user.Id.ToString()), new Claim("Username", user.Username)], "external"));
        var details = new Dictionary<string, object> { ["externalProvider"] = source };

        var membership = await session.Query<Membership>()
            .FirstOrDefaultAsync(m => m.UserId == user.Id && m.TenantSlug == slug, ct);
        if (membership is null)
        {
            await Features.Tenants.Members.Members.AddAsync(session, actor, user.Id, slug, granted, null, details, ct);
            return true;
        }

        if (membership.Status != MembershipStatus.Active)
        {
            return false;
        }

        var missing = granted.Except(membership.RoleIds ?? []).ToList();
        if (missing.Count == 0)
        {
            return false;
        }

        await Features.Tenants.Members.Members.ChangeAsync(
            session, actor, "tenant.member.updated", membership,
            (membership.RoleIds ?? []).Concat(missing).ToList(), membership.Status, null, details, ct);
        return true;
    }
}
