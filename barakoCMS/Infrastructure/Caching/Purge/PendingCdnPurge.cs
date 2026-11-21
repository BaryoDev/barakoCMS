namespace barakoCMS.Infrastructure.Caching.Purge;

/// <summary>
/// A purge that a commit asked for and the sender has not yet delivered (#561).
/// </summary>
/// <remarks>
/// One row per commit, written after the commit in the tenant's own partition, so two commits never
/// race on one row. The sender merges the rows of a tenant. The row holds tags, not content. The same
/// way <c>PendingBuild</c> keeps a build hook, it survives a restart between the commit and the call.
/// </remarks>
internal sealed class PendingCdnPurge
{
    public Guid Id { get; set; }

    /// <summary>The cache tags the commit touched, with the tenant prefix already on them.</summary>
    public List<string> Tags { get; set; } = [];

    public DateTimeOffset WrittenAt { get; set; }

    /// <summary>When a sender took the row. A claim older than the claim timeout is taken again.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>After a failed try, the earliest time the next try may run.</summary>
    public DateTimeOffset? RetryAt { get; set; }

    /// <summary>How many tries have failed.</summary>
    public int Attempts { get; set; }
}
