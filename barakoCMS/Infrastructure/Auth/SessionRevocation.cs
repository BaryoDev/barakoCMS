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

    /// <summary>
    /// Is this a second insert of one id or unique value, at any depth? Marten wraps the Npgsql
    /// error (SQLSTATE 23505) at a depth that varies by command, so the chain is walked.
    /// </summary>
    private static bool IsUniqueViolation(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is JasperFx.DocumentAlreadyExistsException or Npgsql.PostgresException { SqlState: "23505" })
                return true;
        }

        return false;
    }

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
    /// between the read and the write, since the successor is then the row to revoke, and when another
    /// end of the same session inserts the key first, which counts as already ended.
    /// </summary>
    /// <param name="alongside">
    /// Stores more documents (an audit entry) in the same save. It runs on every attempt that has a
    /// live row to revoke, because a retry ejects what an earlier attempt stored, and not at all
    /// when there is none.
    /// </param>
    /// <returns>True when this call revoked a live row, false when the session was already ended.</returns>
    public static async Task<bool> EndAsync(
        IDocumentSession session, IMemoryCache? cache, Guid userId, Guid sessionId, CancellationToken ct,
        Func<IDocumentSession, Task>? alongside = null)
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

            if (alongside is not null && rows.Count > 0)
            {
                await alongside(session);
            }

            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (attempt < Attempts
                && (ConcurrencyConflict.Is(ex) || IsUniqueViolation(ex)))
            {
                // A second end of the same session inserted the key first. The session is ended, and
                // the next pass finds that row and updates it instead of inserting another.
                session.EjectAllPendingChanges();
                continue;
            }

            var ttl = revoke.ExpiresAt - DateTime.UtcNow;
            if (cache is not null && ttl > TimeSpan.Zero)
                cache.Set($"revoked:{key}", true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = 1 });
            return rows.Count > 0;
        }
    }
}
