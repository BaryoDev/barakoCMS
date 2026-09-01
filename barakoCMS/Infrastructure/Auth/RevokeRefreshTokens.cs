using Marten;
using Marten.Patching;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Ends a user's sessions: revokes their active refresh tokens and refuses the access tokens
/// already issued. Called when a password changes or is reset, or MFA is enabled.
/// </summary>
/// <remarks>
/// Revoking refresh tokens alone left a window. An access token already issued stays valid for up
/// to fifteen minutes, so a session stolen before a password change kept working after it.
///
/// Both halves live here rather than at the three call sites, because a caller asking to end
/// somebody's sessions means both and should not have to know there are two mechanisms. Anything
/// added later that calls this gets the second half without being told.
///
/// RefreshToken is single-tenanted, so a plain session covers all of the user's tokens.
/// </remarks>
public static class RevokeRefreshTokens
{
    /// <param name="sessionEpoch">
    /// Optional. When supplied, this instance's cached epoch for the user is dropped so the change
    /// takes effect on the next request rather than after the cache expires. Optional because it is
    /// an optimisation, not the mechanism: the database is the source of truth and other instances
    /// pick the change up when their own cache expires regardless.
    /// </param>
    public static async Task ForUserAsync(
        IDocumentSession session,
        Guid userId,
        string reason,
        CancellationToken ct,
        Services.ISessionEpochService? sessionEpoch = null)
    {
        var tokens = await session.Query<RefreshToken>()
            .Where(t => t.UserId == userId && !t.IsRevoked)
            .ToListAsync(ct);

        foreach (var token in tokens)
        {
            token.IsRevoked = true;
            token.RevokedReason = reason;
            token.RevokedAt = DateTime.UtcNow;
            session.Store(token);
        }

        // Patch rather than load-modify-store: the caller may already be holding this user, and two
        // writers of the same document in one session is a lost update. Patch is also the shape the
        // failed-login counter uses for the same reason.
        session.Patch<User>(userId).Set(u => u.TokensValidFrom, DateTime.UtcNow);

        sessionEpoch?.Invalidate(userId);
    }
}
