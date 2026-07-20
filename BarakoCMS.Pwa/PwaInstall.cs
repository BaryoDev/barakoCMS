namespace BarakoCMS.Pwa;

/// <summary>
/// One record per browser/device that has run the app, keyed by a client-generated
/// <see cref="DeviceId"/>. Launches from the same device bump <see cref="LastSeenAt"/> and
/// <see cref="LaunchCount"/> instead of adding rows. When a signed-in user reports, their identity is
/// captured so the admin can see <em>who</em> installed the app; anonymous reports are kept too for
/// the aggregate. Stored globally (not per-tenant) — the originating tenant is kept as data.
/// </summary>
public class PwaInstall
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable per-browser id the client generates and persists (localStorage). Dedup key.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>The signed-in user at last report, if any (null for anonymous).</summary>
    public Guid? UserId { get; set; }
    public string? Username { get; set; }

    /// <summary>The tenant the report came from (X-Tenant), kept as data since the doc is global.</summary>
    public string? Tenant { get; set; }

    /// <summary>Best-effort platform: ios | android | windows | macos | linux | other.</summary>
    public string? Platform { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>How the app was being viewed at last report: standalone | minimal-ui | fullscreen | browser.</summary>
    public string DisplayMode { get; set; } = "browser";

    /// <summary>True once this device has run the app as an installed PWA (standalone/fullscreen) at least once.</summary>
    public bool Installed { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this device was first seen running installed (null if only ever seen in a browser tab).</summary>
    public DateTime? InstalledAt { get; set; }

    public int LaunchCount { get; set; }
}
