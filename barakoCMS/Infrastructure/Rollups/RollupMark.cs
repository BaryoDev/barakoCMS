namespace barakoCMS.Infrastructure.Rollups;

/// <summary>
/// A header entry whose rollup values may be out of date. Written in the transaction of the write
/// that made them so, and deleted by the drain once it has recomputed them.
/// </summary>
/// <remarks>
/// Keyed by the header's id, so a burst of writes to one header's entries leaves one mark. The drain
/// deletes a mark only when <see cref="MarkedAt"/> is still the value it read, so a mark set again
/// while it ran survives.
/// </remarks>
internal sealed class RollupMark
{
    /// <summary>The id of the header entry.</summary>
    public Guid Id { get; set; }

    /// <summary>The header's content type, as its definition names it.</summary>
    public string HeaderType { get; set; } = string.Empty;

    public DateTimeOffset MarkedAt { get; set; }
}
