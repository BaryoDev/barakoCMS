using System.Globalization;
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

    /// <summary>The setting that names the host a tenant's domain points its CNAME at.</summary>
    public const string DeploymentHostKey = "Tenancy:DeploymentHost";

    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

    /// <summary>
    /// The deployment's own host: <c>Tenancy:DeploymentHost</c>, else the host of <c>App:BaseUrl</c>.
    /// Null when neither is set. A tenant's domain is a CNAME to this host, and it cannot also be
    /// this host or a name under it.
    /// </summary>
    public static string? DeploymentHost(IConfiguration configuration)
    {
        var named = Clean(configuration[DeploymentHostKey]);
        if (named is not null)
            return named;

        var baseUrl = configuration[CanonicalHostKey]?.Trim();
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? Clean(uri.Host) : null;
    }

    /// <summary>
    /// Trimmed and lower-cased, and nothing more. Unlike a claimed domain it keeps a leading
    /// <c>www.</c>, because it is the name a CNAME is pointed at, and <c>www.example.com</c> and
    /// <c>example.com</c> are different targets.
    /// </summary>
    private static string? Clean(string? host)
    {
        var value = host?.Trim().TrimEnd('.').ToLowerInvariant();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// The longest claimable host. A DNS name is at most 253 characters, and the verification record
    /// lives under <c>_barako-challenge.</c> in front of it, so the host leaves room for that label.
    /// </summary>
    public static readonly int MaxHostLength = 253 - (TenantDomain.TxtLabel.Length + 1);

    private const string CanonicalHostKey = "App:BaseUrl";

    /// <summary>Normalises each requested domain, dropping duplicates, and reports every value that is not a domain.</summary>
    public static IReadOnlyList<string> Normalise(IEnumerable<string?> requested, out IReadOnlyList<string> errors) =>
        Normalise(requested, null, out errors);

    /// <summary>
    /// As <see cref="Normalise(IEnumerable{string?}, out IReadOnlyList{string})"/>, and refuses the
    /// deployment's own host and any name under it, since a tenant claiming one would be claiming
    /// the platform. A name with non-ASCII letters is stored in its ASCII (punycode) form, the form
    /// a browser sends as the host. A leading <c>www.</c> is dropped, so <c>www.x.com</c> and
    /// <c>x.com</c> are one domain.
    /// </summary>
    public static IReadOnlyList<string> Normalise(
        IEnumerable<string?> requested, string? deploymentHost, out IReadOnlyList<string> errors)
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
            if (host is not null && host.Any(c => c > 127))
                host = ToAscii(host);

            if (host is null
                || host.Length > MaxHostLength
                || IPAddress.TryParse(host, out _)
                || !host.Contains('.')
                || !host.Split('.').All(label => Label.IsMatch(label)))
            {
                problems.Add($"'{typed}' is not a valid domain name.");
                continue;
            }

            if (deploymentHost is not null && IsUnderOrAt(host, deploymentHost))
            {
                problems.Add($"'{typed}' is this deployment's own host, or a name under it, and cannot be claimed by a tenant.");
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

    /// <summary>
    /// Whether a claimed host is the deployment's host or a name under it. A claimed host has lost its
    /// <c>www.</c>, and so does a request's host when it is looked up, so a deployment on
    /// <c>www.example.com</c> is also refused the apex <c>example.com</c>: a tenant owning that would
    /// be handed the deployment's own requests.
    /// </summary>
    private static bool IsUnderOrAt(string host, string deploymentHost)
    {
        var own = TenantDomainMap.Normalise(deploymentHost);
        return Matches(host, deploymentHost) || (own is not null && Matches(host, own));

        static bool Matches(string host, string name) =>
            host == name || host.EndsWith("." + name, StringComparison.Ordinal);
    }

    private static string? ToAscii(string host)
    {
        try
        {
            return Idn.GetAscii(host);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
