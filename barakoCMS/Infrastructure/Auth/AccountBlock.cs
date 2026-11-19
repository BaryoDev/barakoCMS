using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// A SuperAdmin block (#1366): the account cannot sign in or hold a token until it is lifted.
/// Every sign-in path treats a blocked user as it treats a wrong password, so nothing tells the
/// caller the account exists.
/// </summary>
public static class AccountBlock
{
    public const string BlockedAuditAction = "auth.account.blocked";
    public const string UnblockedAuditAction = "auth.account.unblocked";
    public const string LoginAuditAction = "auth.login.blocked";
    public const string LoginAuditReason = "account_blocked";

    public static bool Is(User? user) => user?.BlockedAt is not null;

    /// <summary>Stages the audit row for a sign-in refused because the account is blocked. The caller saves.</summary>
    public static Task RecordRefusedLoginAsync(
        IDocumentSession session, string tenantSlug, User user, string? ipAddress, CancellationToken ct) =>
        AuditLog.RecordAsync(session, tenantSlug, LoginAuditAction, user.Id, user.Username,
            metadata: new() { ["reason"] = LoginAuditReason }, ipAddress: ipAddress, ct: ct);

    /// <summary>
    /// Reads the stored row, not the object a caller holds, because that object may have been loaded
    /// before the block.
    /// </summary>
    public static Task<bool> IsStoredAsync(IQuerySession session, Guid userId, CancellationToken ct) =>
        session.Query<User>().AnyAsync(u => u.Id == userId && u.BlockedAt != null, ct);

    /// <summary>
    /// The epoch an unblock stamps. Tokens carry a whole-second iat and the epoch check compares
    /// whole seconds, so a token minted in the same second as the unblock would otherwise come back
    /// to life with the account. Rounding up refuses everything minted in that second.
    /// </summary>
    public static DateTime NextWholeSecond(DateTime now) =>
        new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc).AddSeconds(1);
}
