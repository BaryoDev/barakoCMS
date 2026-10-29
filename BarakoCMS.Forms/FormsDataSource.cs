using barakoCMS.Core.Interfaces;

namespace BarakoCMS.Forms;

/// <summary>
/// The <c>forms</c> data source: the content types that accept public submissions, with the fields
/// each asks for, as <c>GET /api/public/forms/{slug}</c> describes them.
/// </summary>
internal sealed class FormsDataSource : IBlockDataSource
{
    public const string SourceId = "forms";

    public string Id => SourceId;

    public BlockDataSourceShape Shape { get; } = new(
    [
        new("slug", "string"),
        new("displayName", "string"),
        new("description", "string"),
        new("fields", "object"),
    ]);

    public string? Capability => FormsCapabilities.ManageForms;
}
