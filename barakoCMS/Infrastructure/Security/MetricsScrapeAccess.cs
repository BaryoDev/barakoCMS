using System.Security.Cryptography;
using System.Text;

namespace barakoCMS.Infrastructure.Security;

/// <summary>What a request to the Prometheus endpoint is entitled to.</summary>
public enum MetricsScrapeDecision
{
    /// <summary>
    /// No scrape key is configured, so the endpoint serves nobody. This is the state a deployment
    /// starts in, which is why it refuses rather than falls through to the old open behaviour.
    /// </summary>
    NotConfigured,

    /// <summary>A key is configured and the caller did not present a matching one.</summary>
    Rejected,

    /// <summary>The caller presented the configured key.</summary>
    Allowed,
}

/// <summary>
/// Decides whether a caller may scrape <c>/metrics</c>. Prometheus output names every route, counts
/// per-endpoint traffic and exposes process internals, so it is an internal scrape target rather
/// than something to publish on the API listener.
///
/// A scraper cannot sign in, so the credential is a shared key set as
/// <c>Metrics:ScrapeKey</c> (env <c>Metrics__ScrapeKey</c>) and presented either as
/// <c>Authorization: Bearer</c>, which Prometheus sends natively via <c>authorization</c> in a
/// scrape config, or in the <c>X-Metrics-Key</c> header.
/// </summary>
public static class MetricsScrapeAccess
{
    public const string Path = "/metrics";
    public const string HeaderName = "X-Metrics-Key";
    public const string ConfigurationKey = "Metrics:ScrapeKey";

    private const string BearerPrefix = "Bearer ";

    public static bool IsMetricsPath(string? path) =>
        string.Equals(path, Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An unset key means refuse, not allow. An operator who upgrades without setting one loses
    /// scraping and reads about it in the release notes; the alternative is that the endpoint stays
    /// open on every deployment that never noticed the setting existed.
    /// </summary>
    public static MetricsScrapeDecision Authorize(string? configuredKey, string? presentedKey)
    {
        if (string.IsNullOrWhiteSpace(configuredKey))
            return MetricsScrapeDecision.NotConfigured;

        if (string.IsNullOrEmpty(presentedKey))
            return MetricsScrapeDecision.Rejected;

        return CryptographicOperations.FixedTimeEquals(
                   Encoding.UTF8.GetBytes(presentedKey),
                   Encoding.UTF8.GetBytes(configuredKey))
            ? MetricsScrapeDecision.Allowed
            : MetricsScrapeDecision.Rejected;
    }

    /// <summary>The key a request carries, from the dedicated header or from a bearer token.</summary>
    public static string? PresentedKey(string? metricsKeyHeader, string? authorizationHeader)
    {
        if (!string.IsNullOrWhiteSpace(metricsKeyHeader))
            return metricsKeyHeader.Trim();

        if (authorizationHeader is not null &&
            authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var token = authorizationHeader[BearerPrefix.Length..].Trim();
            return token.Length == 0 ? null : token;
        }

        return null;
    }
}
