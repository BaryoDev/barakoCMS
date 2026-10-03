namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Reads a host out of something a caller sent, so it can be looked up in <see cref="TenantDomainMap"/>.
/// </summary>
/// <remarks>
/// Both callers answer a stranger: a browser's <c>Origin</c> header and a reverse proxy asking about a
/// TLS server name. A value is accepted only in the shape a stored domain has passed
/// <see cref="TenantDomains.Normalise"/> in, so an IP literal, a port, a path, user info, a wildcard,
/// a trailing dot or surrounding whitespace never reaches the map. Case is folded the way the map
/// folds it, and a leading <c>www.</c> names the same site, as it does for request routing.
/// </remarks>
internal static class TenantDomainLookup
{
    /// <summary>The longest name DNS allows. Anything longer is refused before it is parsed.</summary>
    public const int MaxHostLength = 253;

    private const string Https = "https://";

    /// <summary>The host, lowercased, when it has the shape of a stored domain; otherwise null.</summary>
    /// <remarks>
    /// Returned as given rather than normalised, because <see cref="TenantDomainMap.Find"/> normalises
    /// it. Doing it twice would strip two leading <c>www.</c> labels where routing strips one.
    /// </remarks>
    public static string? BareHost(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > MaxHostLength
            || value.Trim().Length != value.Length
            || value.EndsWith('.'))
        {
            return null;
        }

        _ = TenantDomains.Normalise([value], out var errors);
        return errors.Count == 0 ? value.ToLowerInvariant() : null;
    }

    /// <summary>The host of a browser origin, lowercased, or null when it cannot be a page on a stored domain.</summary>
    /// <remarks>
    /// <c>https</c> only, and no port: a browser leaves the port out of an origin when it is the
    /// default, so <c>https://example.com:8443</c> is a different origin from the domain's site, and
    /// <c>http://example.com</c> is one a network attacker can answer for.
    /// </remarks>
    public static string? OriginHost(string? origin)
    {
        if (string.IsNullOrEmpty(origin)
            || origin.Length > Https.Length + MaxHostLength
            || !origin.StartsWith(Https, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return BareHost(origin[Https.Length..]);
    }
}
