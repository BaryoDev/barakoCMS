using System.Security.Claims;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Caching.Memory;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// A session is one sign-in. Its id rides in every access token as <c>sid</c> and on its refresh
/// row, and ending it refuses those tokens and the row without touching the user's other sessions.
/// </summary>
/// <remarks>
/// Ending writes a <see cref="RevokedToken"/> whose key is <c>sid:&lt;id&gt;</c>. It lives as long as
/// the session can, so a token minted from the session cannot outlive the revoke. The token check
/// asks for the token's jti and its sid in one query.
/// </remarks>
internal static class SessionRevocation
{
    public const string ClaimType = "sid";
    public const string KeyPrefix = "sid:";
    public const string Reason = "session_revoked";

    /// <summary>The longest an access token lives, so a revoke outlasts the last token minted.</summary>
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);

    private const int Attempts = 4;

    public static Claim ClaimFor(Guid sessionId) => new(ClaimType, sessionId.ToString());

    /// <summary>The claims with <c>sid</c> added, the way <c>did</c> comes with the device claims.</summary>
    public static IReadOnlyList<Claim> With(IEnumerable<Claim>? claims, Guid sessionId) =>
        (claims ?? []).Where(c => c.Type != ClaimType).Append(ClaimFor(sessionId)).ToList();

    /// <summary>Whether the session has been ended, read from the store and not the cache.</summary>
    public static Task<bool> IsEndedAsync(IQuerySession session, Guid sessionId, CancellationToken ct)
    {
        var key = $"{KeyPrefix}{sessionId}";
        return session.Query<RevokedToken>().AnyAsync(r => r.TokenJti == key && r.ExpiresAt > DateTime.UtcNow, ct);
    }

    /// <summary>
    /// Ends the session: stores the revoke key and revokes its live refresh rows with reason
    /// <c>session_revoked</c>, in one save. Saves itself, and retries when a refresh rotates the row
    /// between the read and the write, since the successor is then the row to revoke.
    /// </summary>
    public static async Task EndAsync(
        IDocumentSession session, IMemoryCache? cache, Guid userId, Guid sessionId, CancellationToken ct)
    {
        var key = $"{KeyPrefix}{sessionId}";
        for (var attempt = 1; ; attempt++)
        {
            var rows = await session.Query<RefreshToken>()
                .Where(t => t.UserId == userId && t.SessionId == sessionId && !t.IsRevoked)
                .ToListAsync(ct);

            var now = DateTime.UtcNow;
            var expiry = rows.Select(r => r.ExpiresAt).Append(now).Max().Add(AccessLifetime);

            foreach (var row in rows)
            {
                row.IsRevoked = true;
                row.RevokedReason = Reason;
                row.RevokedAt = now;
                session.Store(row);
            }

            var existing = await session.Query<RevokedToken>().FirstOrDefaultAsync(r => r.TokenJti == key, ct);
            var revoke = existing ?? new RevokedToken { Id = Guid.NewGuid(), TokenJti = key, UserId = userId };
            revoke.RevokedAt = now;
            revoke.Reason = Reason;
            if (revoke.ExpiresAt < expiry) revoke.ExpiresAt = expiry;
            session.Store(revoke);

            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (JasperFx.ConcurrencyException) when (attempt < Attempts)
            {
                session.EjectAllPendingChanges();
                continue;
            }

            var ttl = revoke.ExpiresAt - DateTime.UtcNow;
            if (cache is not null && ttl > TimeSpan.Zero)
                cache.Set($"revoked:{key}", true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = 1 });
            return;
        }
    }
}
