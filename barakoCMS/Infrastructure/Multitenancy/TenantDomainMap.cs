namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Custom domain to tenant slug, resolved on every request.
/// </summary>
/// <remarks>
/// Built once and held in memory rather than queried per request. Tenant domains change when an
/// operator edits a tenant, which is rare; a database round trip on the hot path to answer a
/// question whose answer almost never changes is not a trade worth making.
///
/// A domain may belong to exactly one tenant. Allowing two would make resolution depend on
/// enumeration order, which is the same silent-wrong-answer shape as the bug this exists to fix, so
/// construction throws instead.
/// </remarks>
public sealed class TenantDomainMap
{
    public static readonly TenantDomainMap Empty = new(Array.Empty<(string, string)>());

    private readonly Dictionary<string, string> _bySlug;
    private readonly HashSet<string> _activeSlugs;

    public TenantDomainMap(IEnumerable<(string Domain, string Slug)> entries)
        : this(entries, [])
    {
    }

    /// <param name="entries">Each registered domain and the tenant it belongs to.</param>
    /// <param name="activeSlugs">
    /// The slug of every active tenant, with or without a domain, exactly as stored. It rides in the
    /// same cached object so that whether a slug is a tenant and which domain names it are read
    /// from one snapshot and dropped together.
    /// </param>
    public TenantDomainMap(IEnumerable<(string Domain, string Slug)> entries, IEnumerable<string> activeSlugs)
    {
        // Ordinal, and nothing lowercased here: the token issuer matches the stored slug exactly
        // against the lowercased one it is asked for, and the two answers have to agree.
        _activeSlugs = new HashSet<string>(activeSlugs, StringComparer.Ordinal);
        _bySlug = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (domain, slug) in entries)
        {
            var key = Normalise(domain);
            if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(slug))
                continue;

            if (_bySlug.TryGetValue(key, out var existing) && !existing.Equals(slug, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Domain '{key}' is registered to both '{existing}' and '{slug}'. "
                    + "A domain must belong to exactly one tenant.");

            _bySlug[key] = slug.Trim().ToLowerInvariant();
        }
    }

    public int Count => _bySlug.Count;

    /// <summary>Whether <paramref name="slug"/>, trimmed and lowercased, is an active tenant's.</summary>
    public bool IsActiveTenant(string? slug) => slug is not null && _activeSlugs.Contains(slug);

    /// <summary>The tenant slug for a host, or null if the host is not a registered domain.</summary>
    public string? Find(string? host)
    {
        var key = Normalise(host);
        return key is not null && _bySlug.TryGetValue(key, out var slug) ? slug : null;
    }

    /// <summary>
    /// Lower-cases, drops the fully qualified trailing dot, strips any port, and treats a leading
    /// <c>www.</c> as the same site.
    /// </summary>
    /// <remarks>
    /// The <c>www</c> rule is here rather than at the call site because both halves of the comparison
    /// have to agree. Normalising only the incoming host would mean a tenant that registered
    /// "www.abc.com" could never be found.
    /// </remarks>
    internal static string? Normalise(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var value = host.Trim().ToLowerInvariant();

        var colon = value.IndexOf(':');
        if (colon >= 0)
            value = value[..colon];

        value = value.TrimEnd('.');

        if (value.StartsWith("www.", StringComparison.Ordinal))
            value = value[4..];

        return value.Length == 0 ? null : value;
    }
}
