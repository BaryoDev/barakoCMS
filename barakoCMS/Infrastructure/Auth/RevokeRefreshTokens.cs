using Marten;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Revokes a user's active refresh tokens. Called when a password changes or is reset, so that any
/// session established before the change cannot be silently refreshed afterwards. RefreshToken is
/// single-tenanted, so a plain session covers all of the user's tokens.
/// </summary>
public static class RevokeRefreshTokens
{
    public static async Task ForUserAsync(IDocumentSession session, Guid userId, string reason, CancellationToken ct)
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
    }
}
