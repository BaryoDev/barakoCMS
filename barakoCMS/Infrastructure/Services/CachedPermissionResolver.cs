using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Cached decorator for PermissionResolver, with invalidation that outlives what it invalidates.
/// </summary>
/// <remarks>
/// Invalidation used to bump a version counter that formed part of the cache key. The counter was
/// itself an entry in this same cache, with the same five minute expiry, in a store that is also
/// size-bounded and evicts under pressure. Reading it used <c>Get&lt;int&gt;</c>, which returns 0
/// when the entry is gone.
///
/// So the counter could vanish while the decisions keyed on it were still alive. The next
/// invalidation then read 0, wrote 1, and rebuilt exactly the key that was already cached: the
/// revoked permission came back, and the "Invalidated permission cache" line was logged either way.
/// Eviction produced the same resurrection with no timing constraint at all.
///
/// A token cannot be stored in the thing it is supposed to invalidate. This uses the mechanism
/// MemoryCache provides for the purpose instead: every cached decision carries an expiration token,
/// and invalidating cancels it, which evicts every dependent entry at once and deterministically.
/// There is no version arithmetic left to get wrong, and the sources live in a plain dictionary that
/// nothing expires or evicts.
/// </remarks>
public class CachedPermissionResolver : IPermissionResolver
{
    private readonly PermissionResolver _inner;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CachedPermissionResolver> _logger;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private const string CacheKeyPrefix = "perm:";

    // Well-known SuperAdmin role ID (matches DataSeeder.SuperAdminRoleId)
    private static readonly Guid SuperAdminRoleId = barakoCMS.Data.DataSeeder.SuperAdminRoleId;

    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    /// <summary>
    /// One cancellation source per user, plus one global, held outside the cache on purpose.
    /// </summary>
    /// <remarks>
    /// Static because the decorator is resolved per scope while the cache it decorates is a
    /// singleton. A per-instance dictionary would hand each request its own sources, so an
    /// invalidation on one request would cancel tokens no cached entry had ever registered.
    /// </remarks>
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> UserTokens = new();

    private static CancellationTokenSource _globalToken = new();

    public CachedPermissionResolver(
        PermissionResolver inner,
        IMemoryCache cache,
        ILogger<CachedPermissionResolver> logger,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _inner = inner;
        _cache = cache;
        _logger = logger;
        _tenant = tenant;
    }

    /// <summary>
    /// Forwarded, never cached.
    /// </summary>
    /// <remarks>
    /// The inner call is two queries for the roles, and those are already memoised per request on the
    /// resolver itself. What is left is string building, which is cheaper than a cache lookup and has
    /// none of the staleness: this returns a predicate built from the rules as they are right now,
    /// and caching it would reintroduce exactly the window the expiration tokens below exist to close.
    /// </remarks>
    public Task<ReadPredicate> ReadPredicateAsync(
        User user, string contentTypeSlug, CancellationToken cancellationToken = default) =>
        _inner.ReadPredicateAsync(user, contentTypeSlug, cancellationToken);

    /// <summary>
    /// Invalidates all cached permissions for a specific user.
    /// Call this when a user's roles or group memberships change.
    /// </summary>
    public void InvalidateUserPermissions(Guid userId)
    {
        // Cancel first, then replace, so a decision cached between the two registers against the
        // new source rather than one that has already fired and would never evict it.
        if (UserTokens.TryRemove(userId, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }

        _logger.LogInformation("Invalidated permission cache for user {UserId}", userId);
    }

    /// <summary>
    /// Invalidates all permission caches (e.g., when roles are modified).
    /// </summary>
    public void InvalidateAllPermissions()
    {
        var previous = Interlocked.Exchange(ref _globalToken, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();

        // Per-user sources too. A global invalidation means every decision, and an entry registered
        // against both tokens is only evicted when one of them actually fires.
        foreach (var userId in UserTokens.Keys)
        {
            if (UserTokens.TryRemove(userId, out var token))
            {
                token.Cancel();
                token.Dispose();
            }
        }

        _logger.LogInformation("Invalidated all permission caches");
    }

    /// <summary>
    /// Tenant, user, type, action. There is no item in it, because there is no item decision in the
    /// cache any more. Eviction is what invalidates, not a changed key.
    /// </summary>
    private string GetCacheKey(User user, string contentTypeSlug, string action) =>
        $"{CacheKeyPrefix}{_tenant.Slug}:{user.Id}:{contentTypeSlug}:{action}";

    public async Task<bool> CanPerformActionAsync(
        User user,
        string contentTypeSlug,
        string action,
        Content? content = null,
        CancellationToken cancellationToken = default)
    {
        // SuperAdmin bypass - no caching needed (always true)
        if (user.RoleIds != null && user.RoleIds.Contains(SuperAdminRoleId))
        {
            _logger.LogDebug("SuperAdmin bypass for user {UserId}", user.Id);
            return true;
        }

        // An item decision is answered fresh, every time. It can depend on the item's contents:
        // ConditionEvaluator reads status, lastmodifiedby, createdby and any data field off the
        // document, and status and lastmodifiedby both change on an ordinary write. Nothing on the
        // write path invalidates this cache, so a decision keyed on the item's id alone kept
        // granting for up to five minutes after the state it was based on had gone. A stale denial
        // is an inconvenience; a stale grant is an authorisation check that has stopped checking.
        //
        // Keying on the item's version would close it, but the version is not on the document: the
        // document is the fold and the version belongs to the stream, so reading it here costs a
        // query per check and the cache stops paying for itself.
        //
        // It was not paying much anyway. See the remarks on PermissionResolver.RolesForAsync: the
        // key included the item id, so a list was a miss on every row, and what actually fixed the
        // list was memoising the two role queries per request. That memoisation is untouched, and
        // ConditionEvaluator reads dictionaries in memory, so what is left here is no I/O per item.
        if (content != null)
        {
            return await _inner.CanPerformActionAsync(user, contentTypeSlug, action, content, cancellationToken);
        }

        var cacheKey = GetCacheKey(user, contentTypeSlug, action);

        // Check cache
        if (_cache.TryGetValue(cacheKey, out bool cachedResult))
        {
            _logger.LogDebug("Permission cache HIT: {CacheKey} = {Result}", cacheKey, cachedResult);
            return cachedResult;
        }

        // Cache miss - call inner resolver
        _logger.LogDebug("Permission cache MISS: {CacheKey}", cacheKey);
        var result = await _inner.CanPerformActionAsync(user, contentTypeSlug, action, content: null, cancellationToken);

        // Cache the result
        var cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            Size = 1
        };

        // Both tokens are read before the entry is stored. Reading them after would leave a window
        // where an invalidation between the two cancelled a source this entry had not yet
        // registered against, which is the same resurrection in a smaller window.
        cacheOptions.AddExpirationToken(new CancellationChangeToken(TokenFor(user.Id)));
        cacheOptions.AddExpirationToken(new CancellationChangeToken(Volatile.Read(ref _globalToken).Token));

        _cache.Set(cacheKey, result, cacheOptions);
        _logger.LogDebug("Permission cached: {CacheKey} = {Result} (TTL: {Duration})",
            cacheKey, result, CacheDuration);

        return result;
    }

    private const string CapabilityKeyPrefix = "cap:";

    /// <summary>
    /// Cached the same way and under the same expiration tokens as a content decision, so the
    /// invalidation the role and membership endpoints already call evicts capabilities too. That is
    /// what makes revoking a capability take effect without waiting for the caller's token to expire.
    /// </summary>
    public async Task<bool> HasCapabilityAsync(
        Guid userId,
        string capability,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{CapabilityKeyPrefix}{_tenant.Slug}:{userId}:{capability}";

        if (_cache.TryGetValue(cacheKey, out bool cachedResult))
            return cachedResult;

        var result = await _inner.HasCapabilityAsync(userId, capability, cancellationToken);

        var cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            Size = 1
        };
        cacheOptions.AddExpirationToken(new CancellationChangeToken(TokenFor(userId)));
        cacheOptions.AddExpirationToken(new CancellationChangeToken(Volatile.Read(ref _globalToken).Token));

        _cache.Set(cacheKey, result, cacheOptions);

        return result;
    }

    /// <summary>The user's current cancellation token, creating a source if there is not one.</summary>
    private static CancellationToken TokenFor(Guid userId) =>
        UserTokens.GetOrAdd(userId, _ => new CancellationTokenSource()).Token;
}
