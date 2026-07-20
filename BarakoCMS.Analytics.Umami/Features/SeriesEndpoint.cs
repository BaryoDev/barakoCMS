using FastEndpoints;

namespace BarakoCMS.Analytics.Umami.Features;

public sealed class SeriesPointDto
{
    public string X { get; set; } = "";
    public long Y { get; set; }
}

public sealed class SeriesResponse
{
    public string Unit { get; set; } = "day";
    public List<SeriesPointDto> Pageviews { get; set; } = new();
    public List<SeriesPointDto> Sessions { get; set; } = new();
}

/// <summary>GET /api/analytics/{websiteId}/series?range=7d — pageviews/sessions over time, for the trend chart.</summary>
public sealed class SeriesEndpoint : Endpoint<AnalyticsWindowRequest, SeriesResponse>
{
    private readonly IUmamiClient _umami;

    public SeriesEndpoint(IUmamiClient umami) => _umami = umami;

    public override void Configure()
    {
        Get("/api/analytics/{websiteId}/series");
        Roles("Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(AnalyticsWindowRequest req, CancellationToken ct)
    {
        var (startAt, endAt, unit) = AnalyticsRange.Resolve(req.Range);
        var s = await _umami.GetSeriesAsync(req.WebsiteId, startAt, endAt, unit, ct);
        await SendOkAsync(new SeriesResponse
        {
            Unit = unit,
            Pageviews = s.Pageviews.Select(p => new SeriesPointDto { X = p.X, Y = p.Y }).ToList(),
            Sessions = s.Sessions.Select(p => new SeriesPointDto { X = p.X, Y = p.Y }).ToList(),
        }, ct);
    }
}
