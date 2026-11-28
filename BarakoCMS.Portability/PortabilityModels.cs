using barakoCMS.Models;

namespace BarakoCMS.Portability;

/// <summary>One entry of the <c>contents</c> section, recreated under a new id wherever it is applied.</summary>
internal sealed class ContentRecord
{
    /// <summary>
    /// The entry's id where it was exported. Import never reuses it: the entry gets a new id, and a
    /// reference field in the same bundle holding this value is pointed at the new one. Absent in
    /// older bundles, whose references then have to exist where they are imported.
    /// </summary>
    public Guid? Id { get; set; }

    public string ContentType { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public string Status { get; set; } = "Published";

    /// <summary>
    /// Keys in <see cref="Data"/> whose value was masked for the exporting caller. Import leaves
    /// them out, so a mask such as <c>***</c> is never stored as if it were the value.
    /// </summary>
    public List<string> MaskedFields { get; set; } = new();

    /// <summary>
    /// The entry's document-level sensitivity. Import creates the entry at this level, so a restore
    /// does not make a Hidden or Sensitive entry Public. Absent in older bundles, which import as
    /// Public, as they always have.
    /// </summary>
    public SensitivityLevel? Sensitivity { get; set; }
}

/// <summary>The counts a bundle's entries fill in while they are prepared.</summary>
internal sealed class ImportReport
{
    public bool DryRun { get; set; }
    public int ContentTypesCreated { get; set; }
    public int ContentTypesUpdated { get; set; }
    public int ContentsCreated { get; set; }

    /// <summary>
    /// Records whose content type is in neither the store nor this bundle. They are still created,
    /// but with no schema behind them nothing is treated as a public field, so their search text is
    /// empty and they never appear in public search.
    /// </summary>
    public int ContentsWithoutContentType { get; set; }

    /// <summary>The distinct type names behind <see cref="ContentsWithoutContentType"/>.</summary>
    public List<string> UnknownContentTypes { get; set; } = new();
}
