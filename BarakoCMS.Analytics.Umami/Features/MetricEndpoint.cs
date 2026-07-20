using FastEndpoints;

namespace BarakoCMS.Analytics.Umami.Features;

public sealed class MetricRequest
{
    public string WebsiteId { get; set; } = "";
    public string Range { get; set; } = "7d";
    /// <summary>Umami metric type: url | referrer | country | browser | os | device.</summary>
    public string Type { get; set; } = "url";
    public int Limit { get; set; } = 10;
}

public sealed class MetricRow
{
    public string X { get; set; } = "";
    public long Y { get; set; }
}

/// <summary>GET /api/analytics/{websiteId}/metric?type=url&amp;range=7d — a top-N breakdown
/// (pages, referrers, countries, …) for the window.</summary>
public sealed class MetricEndpoint : Endpoint<MetricRequest, List<MetricRow>>
{
    private static readonly HashSet<string> Allowed =
        new(StringComparer.OrdinalIgnoreCase) { "url", "referrer", "country", "browser", "os", "device", "event" };

    private readonly IUmamiClient _umami;

    public MetricEndpoint(IUmamiClient umami) => _umami = umami;

    public override void Configure()
    {
        Get("/api/analytics/{websiteId}/metric");
        Roles("Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(MetricRequest req, CancellationToken ct)
    {
        var type = Allowed.Contains(req.Type) ? req.Type.ToLowerInvariant() : "url";
        var limit = Math.Clamp(req.Limit, 1, 50);
        var (startAt, endAt, _) = AnalyticsRange.Resolve(req.Range);
        var rows = await _umami.GetMetricsAsync(req.WebsiteId, type, startAt, endAt, limit, ct);
        await SendOkAsync(rows.Select(r => new MetricRow { X = r.X, Y = r.Y }).ToList(), ct);
    }
}
