namespace BarakoCMS.Reports;

/// <summary>
/// A saved report over one content type. Stored per tenant and named by <see cref="Name"/>.
/// </summary>
public sealed class ReportDefinition
{
    public const int DefaultGroupLimit = 100;
    public const int MaxGroupLimit = 1000;

    /// <summary>The report's identity and the segment of its route. Lower case letters, digits and hyphens.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string ContentType { get; set; } = string.Empty;

    /// <summary>Equality and range conditions, all of which an entry must meet.</summary>
    public List<ReportFilter> Filters { get; set; } = [];

    /// <summary>The field whose value makes a group, or null for a single row of totals.</summary>
    public string? GroupBy { get; set; }

    /// <summary>At least one. A definition saved with none gets a count.</summary>
    public List<ReportMeasure> Measures { get; set; } = [];

    /// <summary>
    /// The date field <c>from</c> and <c>to</c> apply to, as the content type declares it. Null
    /// applies them to the day the entry was created.
    /// </summary>
    public string? DateField { get; set; }

    /// <summary>The most groups a run returns. The totals still cover every group.</summary>
    public int GroupLimit { get; set; } = DefaultGroupLimit;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid CreatedBy { get; set; }
}

public sealed class ReportFilter
{
    public string Field { get; set; } = string.Empty;

    /// <summary>One of eq, ne, lt, lte, gt, gte.</summary>
    public string Op { get; set; } = "eq";

    public string Value { get; set; } = string.Empty;
}

public sealed class ReportMeasure
{
    /// <summary>One of count, sum, avg, min, max.</summary>
    public string Fn { get; set; } = "count";

    /// <summary>A numeric field. Not used by count.</summary>
    public string? Field { get; set; }

    /// <summary>The name the figure goes by in a run. Defaults to <c>fn_field</c>, or <c>count</c>.</summary>
    public string? As { get; set; }

    public string Label => !string.IsNullOrWhiteSpace(As)
        ? As!
        : string.IsNullOrWhiteSpace(Field) ? Fn : $"{Fn}_{Field}";
}
