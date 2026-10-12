using Microsoft.Extensions.Configuration;
using Marten;
using barakoCMS.Infrastructure.Auth;
using FastEndpoints;

namespace BarakoCMS.Analytics.Umami.Features;

public sealed class MetricRequest : IAnalyticsWindow
{
    public string WebsiteId { get; set; } = "";
    public string Range { get; set; } = "7d";

    /// <summary>The start of an explicit window. Set together with <see cref="To"/>, or not at all.</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>The end of an explicit window.</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>Umami metric type: path | referrer | country | browser | os | device.</summary>
    public string Type { get; set; } = "path";
    public int Limit { get; set; } = 10;
}

internal sealed class MetricRequestValidator : Validator<MetricRequest>
{
    public MetricRequestValidator() => AnalyticsWindowRules.Apply(this);
}

public sealed class MetricRow
{
    public string X { get; set; } = "";
    public long Y { get; set; }
}

/// <summary>GET /api/analytics/{websiteId}/metric?type=url&amp;range=7d — a top-N breakdown
/// (pages, referrers, countries, …) for the window.</summary>
public sealed class MetricEndpoint(
    IUmamiClient umami,
    IQuerySession session,
    IConfiguration configuration) : Endpoint<MetricRequest, List<MetricRow>>
{
    // Umami v3 metric types. Pages are "path" (v2 called it "url").
    private static readonly HashSet<string> Allowed =
        new(StringComparer.OrdinalIgnoreCase) { "path", "referrer", "country", "browser", "os", "device", "event" };

    public override void Configure()
    {
        Get("/api/analytics/{websiteId}/metric");
        Definition.RequireCapability(
            AnalyticsCapabilities.ViewAnalytics, AnalyticsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(MetricRequest req, CancellationToken ct)
    {
        if (!await AnalyticsAccess.AllowedAsync(session, User, configuration, AnalyticsCapabilities.ViewAnalytics, ct))
        {
            ThrowError(PlatformScope.DeploymentWideMessage, 403);
        }

        var type = Allowed.Contains(req.Type) ? req.Type.ToLowerInvariant() : "path";
        var limit = Math.Clamp(req.Limit, 1, 50);
        var (startAt, endAt, _) = AnalyticsRange.Resolve(req.Range, req.From, req.To);
        var rows = await umami.GetMetricsAsync(req.WebsiteId, type, startAt, endAt, limit, ct);
        await Send.OkAsync(rows.Select(r => new MetricRow { X = r.X, Y = r.Y }).ToList(), ct);
    }
}
