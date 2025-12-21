using Microsoft.Extensions.Caching.Memory;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Cached decorator for PermissionResolver.
/// Caches permission check results for 5 minutes to improve performance.
/// </summary>
public class CachedPermissionResolver : IPermissionResolver
{
    private readonly PermissionResolver _inner;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CachedPermissionResolver> _logger;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    
    // Well-known SuperAdmin role ID (should match DataSeeder)
    private static readonly Guid SuperAdminRoleId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public CachedPermissionResolver(
        PermissionResolver inner,
        IMemoryCache cache,
        ILogger<CachedPermissionResolver> logger)
    {
        _inner = inner;
        _cache = cache;
        _logger = logger;
    }

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
            return true;
        }

        // Build cache key
        // Format: perm:userId:contentType:action[:contentId]
        var cacheKey = content == null
            ? $"perm:{user.Id}:{contentTypeSlug}:{action}"
            : $"perm:{user.Id}:{contentTypeSlug}:{action}:{content.Id}";

        // Check cache
        if (_cache.TryGetValue(cacheKey, out bool cachedResult))
        {
            _logger.LogDebug("Permission cache HIT: {CacheKey} = {Result}", cacheKey, cachedResult);
            return cachedResult;
        }

        // Cache miss - call inner resolver
        _logger.LogDebug("Permission cache MISS: {CacheKey}", cacheKey);
        var result = await _inner.CanPerformActionAsync(user, contentTypeSlug, action, content, cancellationToken);

        // Cache the result
        var cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            Size = 1
        };

        _cache.Set(cacheKey, result, cacheOptions);
        _logger.LogDebug("Permission cached: {CacheKey} = {Result} (TTL: {Duration})", 
            cacheKey, result, CacheDuration);

        return result;
    }
}
