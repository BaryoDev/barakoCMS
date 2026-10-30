using barakoCMS.Core.Interfaces;

namespace barakoCMS.Features.Blocks;

/// <summary>
/// The <c>entries</c> data source: published, publicly deliverable entries, as delivery serves them.
/// A block binding (<c>/api/blocks/bindings</c>) picks the type, order and limit.
/// </summary>
internal sealed class EntriesDataSource : IBlockDataSource
{
    public const string SourceId = "entries";

    public string Id => SourceId;

    public BlockDataSourceShape Shape { get; } = new(
    [
        new("id", "id"),
        new("contentType", "string"),
        new("slug", "string"),
        new("data", "object"),
        new("createdAt", "date"),
        new("updatedAt", "date"),
    ]);

    /// <summary>None: the rows are already what an anonymous reader may see.</summary>
    public string? Capability => null;
}
