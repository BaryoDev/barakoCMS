namespace BarakoCMS.FeatureFlags;

/// <summary>
/// A feature flag: a key you can turn on/off, optionally narrowed to specific clubs, users, or a
/// percentage of traffic. Flags are global (one definition), evaluated per request against a
/// <see cref="FlagContext"/>. Empty targeting lists mean "no restriction on that dimension".
/// </summary>
public class FeatureFlag
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable, unique identifier used in code, e.g. "external-auth".</summary>
    public string Key { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Master on/off. When false the flag is off for everyone.</summary>
    public bool Enabled { get; set; }

    /// <summary>On only for these club/tenant slugs (empty = all clubs).</summary>
    public List<string> TenantSlugs { get; set; } = new();

    /// <summary>On only for these user emails (empty = all users).</summary>
    public List<string> UserEmails { get; set; } = new();

    /// <summary>Gradual rollout, 0..100. Below 100, a deterministic slice is on.</summary>
    public int RolloutPercent { get; set; } = 100;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Who a flag is being evaluated for. All parts optional.</summary>
public record FlagContext(string? TenantSlug = null, string? UserEmail = null, string? BucketKey = null);
