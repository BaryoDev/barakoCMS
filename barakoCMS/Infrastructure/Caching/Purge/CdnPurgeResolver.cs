using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Caching.Purge;

/// <summary>
/// Finds the purge client for a tenant, or null when the tenant has purging off.
/// </summary>
/// <remarks>
/// The default tenant reads host configuration (<c>Delivery:CdnPurge:*</c>). Every other tenant reads
/// its own <see cref="CdnPurgeSettings"/> row. The provider's host is a constant or host
/// configuration (<c>Delivery:CdnPurge:BaseUrl:Cloudflare</c> and <c>:Fastly</c>), never a tenant's
/// input. A stored token that cannot be decrypted is logged by name and answers null.
/// </remarks>
internal sealed class CdnPurgeResolver(
    IQuerySession session,
    ISecretProtector protector,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory)
{
    public const string ConfigSection = "Delivery:CdnPurge";

    private static readonly System.Text.RegularExpressions.Regex TargetIdPattern =
        new(@"\A[A-Za-z0-9]{1,64}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsValidTargetId(string? value) => value is not null && TargetIdPattern.IsMatch(value);

    public async Task<ICdnPurger?> ForTenantAsync(string slug, CancellationToken ct) =>
        (await ResolveAsync(slug, ct))?.Purger;

    /// <summary>The client and the target it was built for, so a caller can name the host it sent to.</summary>
    internal async Task<(CdnPurgeTarget Target, ICdnPurger Purger)?> ResolveAsync(string slug, CancellationToken ct)
    {
        var target = await TargetForAsync(slug, ct);
        if (target is null)
        {
            return null;
        }

        var http = httpClientFactory.CreateClient(HttpClientNames.ExternalApi);
        ICdnPurger purger = string.Equals(target.Provider, CdnPurgeProvider.Cloudflare, StringComparison.OrdinalIgnoreCase)
            ? new CloudflarePurger(http, target, loggerFactory.CreateLogger<CloudflarePurger>())
            : new FastlyPurger(http, target, loggerFactory.CreateLogger<FastlyPurger>());
        return (target, purger);
    }

    internal async Task<CdnPurgeTarget?> TargetForAsync(string slug, CancellationToken ct)
    {
        if (string.Equals(slug, Tenant.DefaultSlug, StringComparison.OrdinalIgnoreCase))
        {
            return FromHost();
        }

        var row = await session.LoadAsync<CdnPurgeSettings>(CdnPurgeSettings.IdForTenant(slug), ct);
        if (row is null || !row.Enabled || !IsPurgeable(row.Provider) || !IsValidTargetId(row.TargetId)
            || string.IsNullOrEmpty(row.ProtectedToken))
        {
            return null;
        }

        var token = protector.Unprotect(row.ProtectedToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            loggerFactory.CreateLogger<CdnPurgeResolver>().LogError(
                "A tenant's stored secret for {Section} {Key} could not be decrypted. Secrets:Key or JWT:Key has "
              + "changed since it was saved, and it has to be entered again.", "cdn-purge", "Token");
            return null;
        }

        return Build(row.Provider, row.TargetId, token.Trim());
    }

    private CdnPurgeTarget? FromHost()
    {
        if (!configuration.GetValue($"{ConfigSection}:Enabled", false))
        {
            return null;
        }

        var provider = configuration[$"{ConfigSection}:Provider"]?.Trim();
        var targetId = configuration[$"{ConfigSection}:TargetId"]?.Trim();
        var token = configuration[$"{ConfigSection}:ApiToken"]?.Trim();
        if (!IsPurgeable(provider) || !IsValidTargetId(targetId) || string.IsNullOrEmpty(token))
        {
            return null;
        }

        return Build(provider!, targetId!, token);
    }

    private CdnPurgeTarget Build(string provider, string targetId, string token)
    {
        var cloudflare = string.Equals(provider, CdnPurgeProvider.Cloudflare, StringComparison.OrdinalIgnoreCase);
        var baseUrl = configuration[$"{ConfigSection}:BaseUrl:{(cloudflare ? "Cloudflare" : "Fastly")}"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = cloudflare ? CloudflarePurger.DefaultBaseUrl : FastlyPurger.DefaultBaseUrl;
        }

        return new CdnPurgeTarget(provider.ToLowerInvariant(), targetId, token, baseUrl.Trim());
    }

    private static bool IsPurgeable(string? provider) =>
        string.Equals(provider?.Trim(), CdnPurgeProvider.Cloudflare, StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider?.Trim(), CdnPurgeProvider.Fastly, StringComparison.OrdinalIgnoreCase);
}
