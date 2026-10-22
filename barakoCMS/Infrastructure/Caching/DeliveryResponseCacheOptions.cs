namespace barakoCMS.Infrastructure.Caching;

/// <summary>
/// The in-process response cache for delivery reads, bound from <c>Delivery:ResponseCache</c> (#562).
/// </summary>
/// <remarks>
/// Off unless <see cref="Enabled"/> is set, so an existing deployment behaves exactly as it did.
/// The store is per instance: with several API instances each keeps its own copy and evicts on the
/// writes it handled, so a publish on one instance leaves the others to expire on their own after
/// <see cref="ExpireSeconds"/>.
/// </remarks>
internal sealed class DeliveryResponseCacheOptions
{
    public const string SectionName = "Delivery:ResponseCache";

    public bool Enabled { get; set; }

    /// <summary>How long an entry is kept when no publish evicts it first.</summary>
    public int ExpireSeconds { get; set; } = 60;

    /// <summary>Total bytes the cache may hold, in megabytes.</summary>
    public int SizeLimitMegabytes { get; set; } = 64;

    /// <summary>A body larger than this many kilobytes is served but not kept.</summary>
    public int MaxBodyKilobytes { get; set; } = 512;

    /// <summary>A request whose query string is longer than this is never cached.</summary>
    public int MaxQueryLength { get; set; } = 512;
}
