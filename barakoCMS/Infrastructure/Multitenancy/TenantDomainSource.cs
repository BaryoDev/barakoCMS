using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>Supplies the domain to tenant map to the resolution middleware.</summary>
public interface ITenantDomainSource
{
    Task<TenantDomainMap> GetAsync(CancellationToken ct = default);

    /// <summary>When true, a host that names no known tenant is refused rather than served the default.</summary>
    bool RefuseUnknownHosts { get; }

    /// <summary>Drops the cached map, so the next request rebuilds it.</summary>
    void Invalidate();
}

public sealed class MultitenancyOptions
{
    public const string SectionName = "Multitenancy";

    /// <summary>
    /// Refuse a request whose host matches no tenant, instead of serving the default tenant.
    /// </summary>
    /// <remarks>
    /// Off by default. A single-tenant deployment reaches the app on whatever host the operator
    /// happens to use and has no tenant domains registered, so turning this on by default would
    /// return 404 for every request on upgrade.
    /// </remarks>
    public bool RefuseUnknownHosts { get; set; }

    /// <summary>How long the domain map is held before it is rebuilt.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Builds the domain map from the tenant documents and caches it.
/// </summary>
/// <remarks>
/// The map is read on every request, so it is cached rather than queried each time. Editing a
/// tenant's domains calls <see cref="Invalidate"/>, so the change is visible immediately rather
/// than after the expiry; the expiry is the backstop for a change made by another instance.
/// </remarks>
public sealed class TenantDomainSource : ITenantDomainSource
{
    private const string CacheKey = "barako.tenant-domains";

    private readonly IDocumentStore _store;
    private readonly IMemoryCache _cache;
    private readonly MultitenancyOptions _options;
    private readonly ILogger<TenantDomainSource> _logger;

    public TenantDomainSource(
        IDocumentStore store,
        IMemoryCache cache,
        IOptions<MultitenancyOptions> options,
        ILogger<TenantDomainSource> logger)
    {
        _store = store;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public bool RefuseUnknownHosts => _options.RefuseUnknownHosts;

    private readonly Lock _gate = new();
    private long _version;

    public void Invalidate()
    {
        lock (_gate)
        {
            _version++;
            _cache.Remove(CacheKey);
        }
    }

    public async Task<TenantDomainMap> GetAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue<TenantDomainMap>(CacheKey, out var cached) && cached is not null)
            return cached;

        long version;
        lock (_gate)
        {
            version = _version;
        }

        await using var session = _store.QuerySession();
        var tenants = await session.Query<Tenant>()
            .Where(t => t.IsActive)
            .ToListAsync(ct);

        var map = Build(tenants, _logger);

        // A tenant written while the query above was running has already called Invalidate, and
        // caching this list would put the old one back for the whole cache duration: in Multi, a
        // tenant just switched off would stay served. So a list read before the last Invalidate is
        // used for this request and not kept.
        lock (_gate)
        {
            if (version == _version)
            {
                // Size is mandatory, not optional: the shared IMemoryCache is configured with a
                // SizeLimit, and an entry without a Size throws on Set. One entry, so it counts as one.
                _cache.Set(CacheKey, map, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = _options.CacheDuration,
                    Size = 1,
                });
            }
        }

        return map;
    }

    /// <summary>The map for a list of active tenants.</summary>
    internal static TenantDomainMap Build(IReadOnlyList<Tenant> tenants, ILogger logger)
    {
        var entries = tenants
            .Where(t => t.Domains.Count > 0)
            .SelectMany(t => t.Domains.Select(d => (Domain: d, t.Slug)));

        var slugs = tenants.Select(t => t.Slug).ToList();

        try
        {
            return new TenantDomainMap(entries, slugs);
        }
        catch (InvalidOperationException ex)
        {
            // A duplicate domain is a data problem an operator has to fix. Throwing here would take
            // every request down with it, including the admin request needed to correct it, so the
            // map degrades to no domains and the conflict is logged loudly instead. The active
            // slugs are kept: in Multi an empty map would answer 404 for every tenant.
            logger.LogError(ex, "Tenant domains conflict; custom domain resolution is disabled until it is resolved");
            return new TenantDomainMap([], slugs);
        }
    }
}
