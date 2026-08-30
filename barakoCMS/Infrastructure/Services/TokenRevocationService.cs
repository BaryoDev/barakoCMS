using Marten;
using Microsoft.Extensions.Caching.Memory;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Implementation of token revocation service with database storage and in-memory caching.
/// </summary>
public class TokenRevocationService : ITokenRevocationService
{
    private readonly IDocumentSession _session;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TokenRevocationService> _logger;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    public TokenRevocationService(
        IDocumentSession session,
        IMemoryCache cache,
        ILogger<TokenRevocationService> logger)
    {
        _session = session;
        _cache = cache;
        _logger = logger;
    }

    public async Task RevokeTokenAsync(string jti, Guid userId, string reason, DateTime expiry, CancellationToken ct = default)
    {
        var revokedToken = new RevokedToken
        {
            Id = Guid.NewGuid(),
            TokenJti = jti,
            UserId = userId,
            RevokedAt = DateTime.UtcNow,
            ExpiresAt = expiry,
            Reason = reason
        };

        _session.Store(revokedToken);
        await _session.SaveChangesAsync(ct);

        // Cache the revocation for fast lookup
        var cacheKey = $"revoked:{jti}";
        var ttl = expiry - DateTime.UtcNow;
        if (ttl > TimeSpan.Zero)
        {
            _cache.Set(cacheKey, true, ttl);
        }

        _logger.LogInformation(
            "Token revoked: JTI={Jti}, UserId={UserId}, Reason={Reason}",
            jti, userId, reason);
    }

    /// <summary>PostgreSQL's undefined_table. The schema has not been applied yet.</summary>
    private const string UndefinedTable = "42P01";

    public async Task<bool> IsTokenRevokedAsync(string jti, CancellationToken ct = default)
    {
        var cacheKey = $"revoked:{jti}";

        // Check cache first
        if (_cache.TryGetValue(cacheKey, out bool _))
        {
            _logger.LogDebug("Token revocation cache hit: {Jti}", jti);
            return true;
        }

        // Fallback to database
        try
        {
            var isRevoked = await _session.Query<RevokedToken>()
                .AnyAsync(r => r.TokenJti == jti && r.ExpiresAt > DateTime.UtcNow, ct);

            if (isRevoked)
            {
                // Cache the result
                _cache.Set(cacheKey, true, CacheDuration);
                _logger.LogDebug("Token revocation database hit: {Jti}", jti);
            }

            return isRevoked;
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == UndefinedTable)
        {
            // The one case where "not revoked" is the true answer rather than a guess: with no
            // table, nothing has ever been revoked. This is first run, before the schema apply,
            // which is what the original catch was written for.
            _logger.LogDebug(ex, "Revocation table does not exist yet; nothing can be revoked");
            return false;
        }
        catch (Exception ex)
        {
            // Everything else fails closed. The old catch returned "not revoked" for any exception,
            // so a revoked token was accepted for as long as the store was unreachable, and it said
            // so at Debug, which production does not emit. A logged-out session came back during a
            // database blip and nothing recorded that it had.
            //
            // Throwing rather than returning true, on purpose. Both refuse the request, but a 401
            // tells the caller their session expired, which is a lie that sends them to sign in and
            // fail again. This surfaces as a server error, which is what it is.
            _logger.LogError(ex, "Could not check token revocation for {Jti}; refusing the request", jti);
            throw new InvalidOperationException(
                "Token revocation could not be checked, so the request cannot be authorised.", ex);
        }
    }

    public async Task RevokeAllUserTokensAsync(Guid userId, string reason, CancellationToken ct = default)
    {
        // Note: This is a simplified implementation.
        // In production, you might want to track active tokens per user
        // or implement a user-level revocation timestamp.
        
        _logger.LogWarning(
            "RevokeAllUserTokensAsync called for UserId={UserId}, Reason={Reason}. " +
            "This requires tracking active tokens or implementing user-level revocation.",
            userId, reason);

        // For now, we'll just log this. A full implementation would:
        // 1. Query all active refresh tokens for the user and revoke them
        // 2. Implement a user-level "tokens_revoked_after" timestamp
        
        var refreshTokens = await _session.Query<RefreshToken>()
            .Where(rt => rt.UserId == userId && !rt.IsRevoked && rt.ExpiresAt > DateTime.UtcNow)
            .ToListAsync(ct);

        foreach (var token in refreshTokens)
        {
            token.IsRevoked = true;
            token.RevokedReason = reason;
            token.RevokedAt = DateTime.UtcNow;
            _session.Update(token);
        }

        await _session.SaveChangesAsync(ct);
        
        _logger.LogInformation(
            "Revoked {Count} refresh tokens for UserId={UserId}",
            refreshTokens.Count, userId);
    }
}
