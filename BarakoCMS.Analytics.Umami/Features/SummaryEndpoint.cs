using FastEndpoints;

namespace BarakoCMS.Analytics.Umami.Features;

public sealed class AnalyticsWindowRequest
{
    public string WebsiteId { get; set; } = "";
    /// <summary>"24h" | "7d" (default) | "30d" | "90d".</summary>
    public string Range { get; set; } = "7d";
}

public sealed class StatValue
{
    public long Value { get; set; }
    public long Previous { get; set; }
}

public sealed class SummaryResponse
{
    public StatValue Pageviews { get; set; } = new();
    public StatValue Visitors { get; set; } = new();
    public StatValue Visits { get; set; } = new();
    public StatValue Bounces { get; set; } = new();
    public StatValue TotalTime { get; set; } = new();
}

/// <summary>GET /api/analytics/{websiteId}/summary?range=7d — headline counters for the window.</summary>
public sealed class SummaryEndpoint : Endpoint<AnalyticsWindowRequest, SummaryResponse>
{
    private readonly IUmamiClient _umami;

    public SummaryEndpoint(IUmamiClient umami) => _umami = umami;

    public override void Configure()
    {
        Get("/api/analytics/{websiteId}/summary");
        Roles("Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(AnalyticsWindowRequest req, CancellationToken ct)
    {
        var (startAt, endAt, _) = AnalyticsRange.Resolve(req.Range);
        var s = await _umami.GetSummaryAsync(req.WebsiteId, startAt, endAt, ct);
        await SendOkAsync(new SummaryResponse
        {
            Pageviews = Map(s.Pageviews),
            Visitors = Map(s.Visitors),
            Visits = Map(s.Visits),
            Bounces = Map(s.Bounces),
            TotalTime = Map(s.TotalTime),
        }, ct);
    }

    private static StatValue Map(UmamiStat s) => new() { Value = s.Value, Previous = s.Previous };
}
