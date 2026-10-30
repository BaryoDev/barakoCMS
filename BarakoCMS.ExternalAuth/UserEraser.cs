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
    public async Task<bool> IsProtectedAsync(IQuerySession session, User user, CancellationToken ct)
    {
        var roleIds = await GlobalRoleIdsAsync(session, user, ct);
        if (roleIds.Contains(SystemRoles.SuperAdminRoleId) || roleIds.Contains(SystemRoles.AdminRoleId))
        {
            return true;
        }

        if (roleIds.Count > 0)
        {
            var roles = await session.Query<Role>().Where(r => r.DeletedAt == null && roleIds.Contains(r.Id)).ToListAsync(ct);
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
        foreach (var membership in held)
        {
            var slug = membership.TenantSlug;
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

        var groups = await session.Query<UserGroup>().Where(g => g.UserIds.Contains(id)).ToListAsync(ct);
        foreach (var group in groups)
        {
            group.UserIds.RemoveAll(u => u == id);
            session.Store(group);
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
        var groups = await session.Query<UserGroup>()
            .Where(g => g.DeletedAt == null && (g.UserIds.Contains(id) || groupIds.Contains(g.Id)))
            .ToListAsync(ct);
        foreach (var role in groups.SelectMany(g => g.RoleIds ?? []))
        {
            ids.Add(role);
        }

        return ids;
    }
}
