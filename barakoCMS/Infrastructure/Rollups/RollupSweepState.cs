namespace barakoCMS.Infrastructure.Rollups;

/// <summary>
/// Where a tenant's daily rollup sweep has got to. One document per tenant.
/// </summary>
/// <remarks>
/// A sweep marks the headers of each type in turn, a page at a time. The cursor is committed with
/// each page, so a sweep that stopped part way resumes there on its next run and marks no header twice
/// in a day. A <see cref="Day"/> that differs from today, ahead or behind, starts the sweep over.
/// </remarks>
internal sealed class RollupSweepState
{
    /// <summary>The one id a tenant's state is stored under.</summary>
    public const string Key = "rollups";

    public string Id { get; set; } = Key;

    /// <summary>The local day, in the site zone, this sweep is for.</summary>
    public DateOnly Day { get; set; }

    /// <summary>The header type being swept, or null before the first page of the day.</summary>
    public string? HeaderType { get; set; }

    /// <summary>The last header of <see cref="HeaderType"/> marked, or null at its start.</summary>
    public Guid? AfterId { get; set; }

    /// <summary>True once every type due today has been swept.</summary>
    public bool Done { get; set; }
}
