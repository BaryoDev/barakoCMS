using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.ExternalAuth;

/// <summary>
/// Erases a user account: the credentials, memberships and personal records go in one transaction,
/// and the <see cref="User"/> row stays as a tombstone with every personal field cleared.
/// </summary>
/// <remarks>
/// Core has no account erasure, so this is the only place one is defined. It queues everything on
/// the caller's session and does not save, so the caller commits the erasure together with whatever
/// else has to land with it (here, the status of the deletion request).
///
/// Why a tombstone and not a delete: the session epoch is read from the user row, and a missing row
/// reads as "never invalidated", which would leave an access token already issued working for up to
/// its lifetime. The tombstone keeps <see cref="User.TokensValidFrom"/>, and keeps the id that
/// content, ledger rows and audit entries refer to resolving to something. The username and email
/// are replaced with values nobody can sign in with, because both carry unique indexes.
///
/// What is deleted: the user's memberships, API keys, MFA secret, social profile, external identity
/// links, account manager grants, group listings, and the sign-in codes, magic links and pending
/// registrations held for their email. What is revoked: refresh tokens, through
/// <see cref="RevokeRefreshTokens"/>, which also bumps the epoch. What is kept: audit entries (a
/// tamper-evident chain), authored content, files and ledger rows, which belong to the tenant and
/// refer to the user by id only, and revoked-token records, which stop a token until it expires.
/// </remarks>
internal sealed class UserEraser(ISessionEpochService sessionEpoch)
{
    public const string AuditAction = "user.erased";

    private static readonly string[] PlatformCapabilities =
    [
        SystemCapabilities.All, SystemCapabilities.ManageRoles, SystemCapabilities.ManagePlatformRoles,
        SystemCapabilities.ManageTenants, SystemCapabilities.ManageUsers,
    ];

    /// <summary>
    /// Whether the account must not be erased by a machine: a platform administrator, or the last
    /// administrator of a tenant. Those need a person to hand the role on first.
    /// </summary>
    /// <remarks>
    /// Two administrators of one tenant erased at the same moment would each count the other as the
    /// one who remains, and both go. So before the count it takes a transaction-scoped Postgres
    /// advisory lock per tenant the user administers, in sorted tenant order so two erasures that
    /// share tenants cannot deadlock. The lock is held until the session saves, which is the same
    /// commit that erases the user, so the second erasure counts after the first has landed. A
    /// caller that does not go on to save must not keep the session open.
    /// </remarks>
    public async Task<bool> IsProtectedAsync(IDocumentSession session, User user, CancellationToken ct)
    {
        var roleIds = await GlobalRoleIdsAsync(session, user, ct);
        if (roleIds.Contains(SystemRoles.SuperAdminRoleId) || roleIds.Contains(SystemRoles.AdminRoleId))
        {
            return true;
        }

        if (roleIds.Count > 0)
        {
            // Roles are tenanted and this runs in the default tenant (the sweeper and the anonymous
            // route have no other), so a role defined in another tenant is found with AnyTenant.
            var roles = await session.Query<Role>()
                .Where(r => r.AnyTenant() && r.DeletedAt == null && roleIds.Contains(r.Id))
                .ToListAsync(ct);
            if (roles.Any(r => r.IsPlatform || (r.SystemCapabilities ?? []).Any(c =>
                    PlatformCapabilities.Contains(c, StringComparer.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        var admin = SystemRoles.AdminRoleId;
        var held = await session.Query<Membership>()
            .Where(m => m.UserId == user.Id && m.Status == MembershipStatus.Active && m.RoleIds.Contains(admin))
            .ToListAsync(ct);
        var tenants = held.Select(m => m.TenantSlug).Distinct().Order(StringComparer.Ordinal).ToList();
        if (tenants.Count > 0)
        {
            await session.BeginTransactionAsync(ct);
            foreach (var slug in tenants)
            {
                await session.QueryAsync<int>(
                    "select 1 from pg_advisory_xact_lock(hashtextextended(?, 0))", ct, AdminLockKey(slug));
            }
        }

        foreach (var slug in tenants)
        {
            var others = await session.Query<Membership>()
                .CountAsync(m => m.TenantSlug == slug && m.UserId != user.Id
                    && m.Status == MembershipStatus.Active && m.RoleIds.Contains(admin), ct);
            if (others == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The advisory lock key that serialises erasures of administrators of one tenant.</summary>
    internal static string AdminLockKey(string tenantSlug) => $"barakocms:tenant-admins:{tenantSlug}";

    /// <summary>Queues the erasure on <paramref name="session"/>. The caller saves.</summary>
    public async Task QueueEraseAsync(IDocumentSession session, User user, string reason, CancellationToken ct)
    {
        var id = user.Id;
        var emails = new List<string> { user.Email, user.NormalizedEmail };

        await RevokeRefreshTokens.ForUserAsync(session, id, "account_erased", ct, sessionEpoch);

        session.DeleteWhere<Membership>(m => m.UserId == id);
        session.DeleteWhere<ApiKey>(k => k.UserId == id);
        session.DeleteWhere<TenantAccountManager>(m => m.UserId == id);
        session.DeleteWhere<SocialProfile>(p => p.UserId == id);
        session.DeleteWhere<ExternalIdentity>(i => i.UserId == id);
        session.DeleteWhere<OtpCode>(o => emails.Contains(o.Email));
        session.DeleteWhere<MagicLinkToken>(t => emails.Contains(t.Email));
        session.DeleteWhere<PendingRegistration>(r => emails.Contains(r.Email));
        session.Delete<MfaSecret>(id);

        // Groups are tenanted, and the user may be listed in a group of any tenant. Each edit is
        // saved against the tenant the group lives in: storing it on this session would write a
        // copy into the session's tenant and leave the id in the original.
        var groups = await session.Query<UserGroup>().Where(g => g.AnyTenant() && g.UserIds.Contains(id)).ToListAsync(ct);
        foreach (var group in groups)
        {
            group.UserIds.RemoveAll(u => u == id);
            var tenantId = (await session.MetadataForAsync(group, ct))?.TenantId;
            if (string.IsNullOrEmpty(tenantId) || tenantId == session.TenantId)
            {
                session.Store(group);
            }
            else
            {
                session.ForTenant(tenantId).Store(group);
            }
        }

        var token = id.ToString("N");
        user.Username = $"erased-{token}";
        user.Email = $"erased-{token}@erased.invalid";
        user.DisplayName = null;
        user.PasswordHash = string.Empty;
        user.RoleIds = [];
        user.GroupIds = [];
        user.FailedLoginAttempts = 0;
        user.LockoutUntil = null;
        user.TokensValidFrom = DateTime.UtcNow;
        session.Store(user);

        // No provider account id and no name in the entry: it says which user and why.
        await AuditLog.RecordAsync(session, Tenant.DefaultSlug, AuditAction, actorUserId: null, actorUsername: null,
            targetType: "User", targetId: id.ToString(),
            metadata: new Dictionary<string, object> { ["reason"] = reason }, ct: ct);
    }

    private static async Task<HashSet<Guid>> GlobalRoleIdsAsync(IQuerySession session, User user, CancellationToken ct)
    {
        var ids = new HashSet<Guid>(user.RoleIds ?? []);
        var groupIds = user.GroupIds ?? [];
        var id = user.Id;
        // Across tenants, for the reason given at the role query in IsProtectedAsync: a platform
        // role granted through a group in any tenant makes the person a platform administrator.
        var groups = await session.Query<UserGroup>()
            .Where(g => g.AnyTenant() && g.DeletedAt == null && (g.UserIds.Contains(id) || groupIds.Contains(g.Id)))
            .ToListAsync(ct);
        foreach (var role in groups.SelectMany(g => g.RoleIds ?? []))
        {
            ids.Add(role);
        }

        return ids;
    }
}
