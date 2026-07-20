namespace BarakoCMS.Analytics.Umami;

/// <summary>
/// Configuration for the Umami analytics module, bound from the "Umami" configuration section.
/// When <see cref="Enabled"/> is false (or the base URL is blank) the endpoints report the module as
/// not configured instead of trying to reach Umami — so the module ships safely turned off.
/// </summary>
public sealed class UmamiOptions
{
    public const string SectionName = "Umami";

    /// <summary>Turn the integration on. Off by default so the module is inert until configured.</summary>
    public bool Enabled { get; set; }

    /// <summary>Base URL of the Umami instance, e.g. http://umami:3000 (server-to-server) — no trailing path.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Umami account used to read stats. Kept server-side; never sent to the browser.</summary>
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>
    /// Public URL the tracking script is served from, used only to build the copy-paste snippet shown
    /// in the admin when adding a site. Falls back to <see cref="BaseUrl"/> when unset.
    /// </summary>
    public string? PublicUrl { get; set; }

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BaseUrl);
}
