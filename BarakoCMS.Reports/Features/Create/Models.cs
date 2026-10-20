namespace BarakoCMS.Reports.Features.Create;

internal sealed class Request
{
    public string Name { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public List<ReportFilter> Filters { get; set; } = [];
    public string? GroupBy { get; set; }
    public List<ReportMeasure> Measures { get; set; } = [];
    public string? DateField { get; set; }
    public int? GroupLimit { get; set; }
}

internal sealed class Response
{
    public string Name { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public List<ReportFilter> Filters { get; set; } = [];
    public string? GroupBy { get; set; }
    public List<ReportMeasure> Measures { get; set; } = [];
    public string? DateField { get; set; }
    public int GroupLimit { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static Response From(ReportDefinition r) => new()
    {
        Name = r.Name,
        Title = r.Title,
        ContentType = r.ContentType,
        Filters = r.Filters,
        GroupBy = r.GroupBy,
        Measures = r.Measures,
        DateField = r.DateField,
        GroupLimit = r.GroupLimit,
        CreatedAt = r.CreatedAt,
    };
}
