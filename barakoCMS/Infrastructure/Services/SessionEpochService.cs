using Marten;
using Microsoft.Extensions.Caching.Memory;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Answers "were tokens issued before this instant invalidated for this user".
/// </summary>
public interface ISessionEpochService
{
    /// <summary>
    /// The instant before which this user's access tokens are refused, or null if nothing has ever
    /// invalidated them.
    /// </summary>
    Task<DateTime?> ValidFromAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Drops the cached answer for a user, so the next read comes from the database.</summary>
    void Invalidate(Guid userId);
}

/// <summary>
/// Reads <see cref="Models.User.TokensValidFrom"/>, cached, because this runs on every authenticated
/// request and a database round trip per request is not affordable.
/// </summary>
/// <remarks>
/// The cache duration is the security property, so it is worth being precise about what this buys.
/// Before it, a stolen access token survived a password change for up to fifteen minutes, the
/// token's own lifetime. With it, the exposure is the cache duration instead, thirty seconds, and on
/// the instance that performed the change it is zero because that instance evicts its own entry.
///
/// It is not zero across instances, and it cannot be with an in-memory cache: the other instances
/// hold their own copy and nothing tells them. Shortening the window from fifteen minutes to thirty
/// seconds is the whole of what this delivers, and calling it more than that would be wrong.
///
/// A null result is cached too. Most users have never had a security event, so the common path must
/// not be a database read that finds nothing every thirty seconds.
/// </remarks>
public sealed class SessionEpochService : ISessionEpochService
{
    private readonly IQuerySession _session;
    private readonly IMemoryCache _cache;

    /// <summary>How stale an answer may be, and therefore how long a token outlives its revocation.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public SessionEpochService(IQuerySession session, IMemoryCache cache)
    {
        _session = session;
        _cache = cache;
    }

    private static string Key(Guid userId) => $"session-epoch:{userId}";

    public async Task<DateTime?> ValidFromAsync(Guid userId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue<DateTime?>(Key(userId), out var cached))
            return cached;

        // Projected rather than loading the User, so this does not pull a whole document, and so a
        // cached User elsewhere in the session cannot answer with a stale copy of the field.
        var validFrom = await _session.Query<Models.User>()
            .Where(u => u.Id == userId)
            .Select(u => u.TokensValidFrom)
            .FirstOrDefaultAsync(ct);

        // Size is required: AddMemoryCache sets SizeLimit, and an entry without one throws. That
        // is not hypothetical, it is what this line did first, and the middleware's catch turned the
        // exception into a control that silently never fired.
        _cache.Set(Key(userId), validFrom, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            Size = 1,
        });
        return validFrom;
    }

    public void Invalidate(Guid userId) => _cache.Remove(Key(userId));
}
