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

    /// <summary>
    /// May an unauthenticated caller see this flag at all? False unless someone deliberately
    /// publishes it, because the key name is the leak: an unreleased feature or a customer name is
    /// disclosed by <c>{"key": false}</c> just as thoroughly as by <c>{"key": true}</c>. An existing
    /// flag stored before this field existed reads back as false, so upgrading publishes nothing.
    /// </summary>
    public bool IsPublic { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Who a flag is being evaluated for. All parts optional.</summary>
public record FlagContext(string? TenantSlug = null, string? UserEmail = null, string? BucketKey = null);

/// <summary>Which flags a caller may be told about, as opposed to what each one evaluates to.</summary>
public enum FlagAudience
{
    /// <summary>Only flags marked <see cref="FeatureFlag.IsPublic"/>.</summary>
    Public,

    /// <summary>Every flag.</summary>
    Authenticated,
}
