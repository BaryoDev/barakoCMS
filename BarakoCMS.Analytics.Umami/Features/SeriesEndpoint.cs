using Microsoft.Extensions.Configuration;
using Marten;
using barakoCMS.Infrastructure.Auth;
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

    /// <summary>The window these points cover, so a caller can ask for the one before it.</summary>
    public DateTimeOffset From { get; set; }

    /// <summary>The end of that window.</summary>
    public DateTimeOffset To { get; set; }

    /// <summary>
    /// The window of the same length just before this one, with the same unit. Only with
    /// <c>compare=previous</c>, and left out otherwise.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PreviousSeries? Previous { get; set; }
}

public sealed class PreviousSeries
{
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public List<SeriesPointDto> Pageviews { get; set; } = new();
    public List<SeriesPointDto> Sessions { get; set; } = new();
}

/// <summary>GET /api/analytics/{websiteId}/series?range=7d: pageviews and sessions over time, for the trend chart.
/// <c>from</c> and <c>to</c> name any window instead, and <c>compare=previous</c> adds the window before it.</summary>
public sealed class SeriesEndpoint(
    IUmamiClient umami,
    IQuerySession session,
    IConfiguration configuration) : Endpoint<AnalyticsWindowRequest, SeriesResponse>
{
    public override void Configure()
    {
        Get("/api/analytics/{websiteId}/series");
        Definition.RequireCapability(
            AnalyticsCapabilities.ViewAnalytics, AnalyticsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(AnalyticsWindowRequest req, CancellationToken ct)
    {
        if (!await AnalyticsAccess.AllowedAsync(session, User, configuration, AnalyticsCapabilities.ViewAnalytics, ct))
        {
            ThrowError(PlatformScope.DeploymentWideMessage, 403);
        }

        var (startAt, endAt, unit) = AnalyticsRange.Resolve(req.Range, req.From, req.To);
        var s = await umami.GetSeriesAsync(req.WebsiteId, startAt, endAt, unit, ct);
        var response = new SeriesResponse
        {
            Unit = unit,
            Pageviews = Points(s.Pageviews),
            Sessions = Points(s.Sessions),
            From = AnalyticsRange.At(startAt),
            To = AnalyticsRange.At(endAt),
        };

        if (string.Equals(req.Compare, "previous", StringComparison.OrdinalIgnoreCase))
        {
            var (previousStart, previousEnd) = AnalyticsRange.Previous(startAt, endAt);
            var previous = await umami.GetSeriesAsync(req.WebsiteId, previousStart, previousEnd, unit, ct);
            response.Previous = new PreviousSeries
            {
                From = AnalyticsRange.At(previousStart),
                To = AnalyticsRange.At(previousEnd),
                Pageviews = Points(previous.Pageviews),
                Sessions = Points(previous.Sessions),
            };
        }

        await Send.OkAsync(response, ct);
    }

    private static List<SeriesPointDto> Points(IReadOnlyList<UmamiSeriesPoint> points) =>
        points.Select(p => new SeriesPointDto { X = p.X, Y = p.Y }).ToList();
}
