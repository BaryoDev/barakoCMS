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
    /// a role that does not exist or was deleted, one that belongs to another tenant, and one carrying
    /// a platform capability or SuperAdmin, which only a SuperAdmin hands out.
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
                || role.DeletedAt is not null
                || !barakoCMS.Features.Roles.RoleScope.Reaches(role, tenantSlug)
                || !barakoCMS.Features.Tenants.Members.Members.IsAssignable(role.Id)
                || barakoCMS.Features.Users.PlatformRoles.CarriesPlatformCapability(role);
        }).ToList();
    }

    /// <summary>Stages the roles the person should hold in the tenant. Nothing is saved.</summary>
    /// <remarks>
    /// An existing membership is read again under a row lock, in a transaction the caller's next
    /// save commits, so what this reports and audits is what that save writes. A membership an
    /// administrator suspended, or gave the role to, after the first read is seen as it now is.
    /// </remarks>
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

        membership = await LockedAsync(session, membership.Id, ct);
        if (membership is not { Status: MembershipStatus.Active })
        {
            return false;
        }

        var missing = granted.Except(membership.RoleIds ?? []).ToList();
        if (missing.Count == 0)
        {
            return false;
        }

        if (membership.RoleIds is null)
        {
            await Features.Tenants.Members.Members.ChangeAsync(
                session, actor, "tenant.member.updated", membership, missing, membership.Status, null, details, ct);
            return true;
        }

        await Features.Tenants.Members.Members.AppendRolesAsync(
            session, actor, "tenant.member.updated", membership, missing, details, ct);
        return true;
    }

    /// <summary>
    /// The membership as stored now, locked until the session's transaction ends. Opens one when
    /// the session has none; inside a caller's transaction it joins that one.
    /// </summary>
    private static async Task<Membership?> LockedAsync(IDocumentSession session, Guid id, CancellationToken ct)
    {
        await session.BeginTransactionAsync(ct);
        var table = $"{session.DocumentStore.Options.DatabaseSchemaName}.mt_doc_memberships";
        await session.QueryAsync<int>($"select 1 from {table} where id = ? for update", ct, id);
        return await session.LoadAsync<Membership>(id, ct);
    }
}
