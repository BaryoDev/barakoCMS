namespace BarakoCMS.Diagnostics;

/// <summary>
/// A client-side (browser) error captured from an app and stored for later triage. Errors are
/// deduplicated by <see cref="Fingerprint"/>: repeated occurrences of the same fault bump
/// <see cref="Count"/> and <see cref="LastSeenAt"/> instead of creating a new row, so the table
/// stays small and the noisiest problems rise to the top.
///
/// Stored globally (not per-tenant) so a platform operator sees errors across every club in one
/// place; the originating club is kept on <see cref="Tenant"/> as data.
/// </summary>
public class ClientError
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable hash of the fault (kind + message + source + status) used to deduplicate.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>What produced it: "error", "unhandledrejection", "react", or "api".</summary>
    public string Kind { get; set; } = "error";

    /// <summary>"error" or "warning".</summary>
    public string Severity { get; set; } = "error";

    public string Message { get; set; } = string.Empty;
    public string? Stack { get; set; }

    /// <summary>Where it came from — a script URL, a component name, or an API path.</summary>
    public string? Source { get; set; }

    /// <summary>HTTP status, when the fault is an API error.</summary>
    public int? Status { get; set; }

    /// <summary>The page URL the user was on.</summary>
    public string? Url { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>App release identifier, so a fixed error can be told apart from a recurring one.</summary>
    public string? AppVersion { get; set; }

    /// <summary>Club/tenant slug the app was scoped to when it happened (may be null pre-login).</summary>
    public string? Tenant { get; set; }

    public Guid? UserId { get; set; }
    public string? Username { get; set; }

    public int Count { get; set; } = 1;
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    public bool Resolved { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
