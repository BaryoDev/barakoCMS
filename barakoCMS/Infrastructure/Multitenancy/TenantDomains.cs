using System.Net;
using System.Text.RegularExpressions;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>Validation and normalisation for the domains a tenant answers on.</summary>
/// <remarks>
/// A domain is stored in the form <see cref="TenantDomainMap"/> matches on, so what an operator reads
/// back is exactly what a request host is compared against.
///
/// A value carrying a scheme, port, path or wildcard is refused rather than trimmed down to a host.
/// Keeping part of what was typed would hide the mistake from the person who made it, and a wildcard
/// is a promise the map cannot keep.
/// </remarks>
public static class TenantDomains
{
    /// <summary>How many domains one tenant may hold. Enough for a brand, its old names and a few regions.</summary>
    public const int MaxPerTenant = 20;

    private static readonly char[] NotInAHost = { ':', '/', '\\', '*', '@', '?', '#', ' ' };

    private static readonly Regex Label = new("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled);

    /// <summary>Normalises each requested domain, dropping duplicates, and reports every value that is not a domain.</summary>
    public static IReadOnlyList<string> Normalise(IEnumerable<string?> requested, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        var result = new List<string>();

        foreach (var raw in requested)
        {
            var typed = raw?.Trim() ?? string.Empty;
            if (typed.Length == 0)
            {
                problems.Add("A domain cannot be empty.");
                continue;
            }

            if (typed.IndexOfAny(NotInAHost) >= 0)
            {
                problems.Add($"'{typed}' is not a bare host. Enter it like example.com, without a scheme, port, path or wildcard.");
                continue;
            }

            var host = TenantDomainMap.Normalise(typed);
            if (host is null
                || host.Length > 253
                || IPAddress.TryParse(host, out _)
                || !host.Contains('.')
                || !host.Split('.').All(label => Label.IsMatch(label)))
            {
                problems.Add($"'{typed}' is not a valid domain name.");
                continue;
            }

            if (!result.Contains(host))
                result.Add(host);
        }

        if (result.Count > MaxPerTenant)
            problems.Add($"A tenant can have at most {MaxPerTenant} domains.");

        errors = problems;
        return result;
    }
}
