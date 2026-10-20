namespace BarakoCMS.CdnPurge;

/// <summary>
/// Settings under <c>Modules:CdnPurge</c>. Inert until <see cref="ZoneId"/> and <see cref="ApiToken"/> are set.
/// </summary>
public sealed class CdnPurgeOptions
{
    /// <summary>The Cloudflare zone to purge.</summary>
    public string ZoneId { get; set; } = "";

    /// <summary>A Cloudflare API token allowed to purge cache on the zone. Never logged.</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>The Cloudflare API root. Changed only to point a test at a stand-in.</summary>
    public string BaseUrl { get; set; } = "https://api.cloudflare.com/client/v4/";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ZoneId) && !string.IsNullOrWhiteSpace(ApiToken);
}
