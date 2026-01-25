using Microsoft.Extensions.Caching.Memory;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Cached decorator for PermissionResolver.
/// Caches permission check results for 5 minutes to improve performance.
/// Supports cache invalidation when user permissions change.
/// </summary>
public class CachedPermissionResolver : IPermissionResolver
{
    private readonly PermissionResolver _inner;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CachedPermissionResolver> _logger;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private const string CacheKeyPrefix = "perm:";

    // Well-known SuperAdmin role ID (matches DataSeeder.SuperAdminRoleId)
    private static readonly Guid SuperAdminRoleId = barakoCMS.Data.DataSeeder.SuperAdminRoleId;

    public CachedPermissionResolver(
        PermissionResolver inner,
        IMemoryCache cache,
        ILogger<CachedPermissionResolver> logger)
    {
        _inner = inner;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Invalidates all cached permissions for a specific user.
    /// Call this when a user's roles or group memberships change.
    /// </summary>
    public void InvalidateUserPermissions(Guid userId)
    {
        // Since IMemoryCache doesn't support pattern-based removal,
        // we use a version key approach
        var versionKey = $"perm_version:{userId}";
        var currentVersion = _cache.Get<int>(versionKey);
        _cache.Set(versionKey, currentVersion + 1, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            Size = 1
        });
        _logger.LogInformation("Invalidated permission cache for user {UserId}", userId);
    }

    /// <summary>
    /// Invalidates all permission caches (e.g., when roles are modified).
    /// </summary>
    public void InvalidateAllPermissions()
    {
        var globalVersionKey = "perm_version:global";
        var currentVersion = _cache.Get<int>(globalVersionKey);
        _cache.Set(globalVersionKey, currentVersion + 1, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            Size = 1
        });
        _logger.LogInformation("Invalidated all permission caches");
    }

    private string GetCacheKey(User user, string contentTypeSlug, string action, Content? content)
    {
        // Include version keys in cache key for invalidation support
        var userVersion = _cache.Get<int>($"perm_version:{user.Id}");
        var globalVersion = _cache.Get<int>("perm_version:global");

        return content == null
            ? $"{CacheKeyPrefix}{user.Id}:{contentTypeSlug}:{action}:v{userVersion}_{globalVersion}"
            : $"{CacheKeyPrefix}{user.Id}:{contentTypeSlug}:{action}:{content.Id}:v{userVersion}_{globalVersion}";
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
            _logger.LogDebug("SuperAdmin bypass for user {UserId}", user.Id);
            return true;
        }

        // Build cache key with version for invalidation support
        var cacheKey = GetCacheKey(user, contentTypeSlug, action, content);

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
