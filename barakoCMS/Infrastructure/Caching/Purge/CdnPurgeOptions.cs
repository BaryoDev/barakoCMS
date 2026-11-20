namespace barakoCMS.Infrastructure.Caching.Purge;

/// <summary>
/// The knobs of the purge sender, read from <c>Delivery:CdnPurge</c> each time they are needed, so a
/// change to configuration reaches a running host.
/// </summary>
internal sealed record CdnPurgeOptions(
    TimeSpan Debounce,
    TimeSpan Timeout,
    int MaxAttempts,
    int MaxTagsPerFlush,
    TimeSpan IdlePoll,
    TimeSpan RetryBackoff)
{
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    public static CdnPurgeOptions From(IConfiguration configuration)
    {
        var section = CdnPurgeResolver.ConfigSection;
        return new CdnPurgeOptions(
            Debounce: TimeSpan.FromMilliseconds(Math.Max(0, configuration.GetValue($"{section}:DebounceMs", 2000))),
            Timeout: TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue($"{section}:TimeoutSeconds", 10))),
            MaxAttempts: Math.Max(1, configuration.GetValue($"{section}:MaxAttempts", 5)),
            MaxTagsPerFlush: Math.Max(1, configuration.GetValue($"{section}:MaxTagsPerFlush", 500)),
            IdlePoll: TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue($"{section}:PollSeconds", 15))),
            RetryBackoff: TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue($"{section}:RetryBackoffSeconds", 30))));
    }

    /// <summary>The wait after <paramref name="attempt"/> failed tries: the base, doubling, up to an hour.</summary>
    public TimeSpan BackoffFor(int attempt)
    {
        var seconds = RetryBackoff.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }
}

/// <summary>Keeps one tenant's tag set inside the cap.</summary>
internal static class CdnPurgeBounds
{
    /// <summary>
    /// Past the cap, drops the entry tags and keeps the rest (type, sitemap, untyped references,
    /// redirects). Past the cap again, the bare tenant tag, which every delivery read carries, so one
    /// purge empties the tenant. <paramref name="level"/> says which of the three applied.
    /// </summary>
    public static IReadOnlyList<string> Collapse(IEnumerable<string> tags, string tenant, int max, out string level)
    {
        var all = tags.Distinct(StringComparer.Ordinal).ToList();
        if (all.Count <= max)
        {
            level = "none";
            return all;
        }

        var entryPrefix = DeliveryCache.TenantTag(tenant) + ":entry:";
        var withoutEntries = all.Where(t => !t.StartsWith(entryPrefix, StringComparison.Ordinal)).ToList();
        if (withoutEntries.Count <= max)
        {
            level = "type-tags";
            return withoutEntries;
        }

        level = "tenant";
        return [DeliveryCache.TenantTag(tenant)];
    }
}
