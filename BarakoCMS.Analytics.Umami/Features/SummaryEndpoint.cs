using Microsoft.Extensions.Configuration;
using Marten;
using barakoCMS.Infrastructure.Auth;
using FastEndpoints;

namespace BarakoCMS.Analytics.Umami.Features;

public sealed class AnalyticsWindowRequest : IAnalyticsWindow
{
    public string WebsiteId { get; set; } = "";
    /// <summary>"24h" | "7d" (default) | "30d" | "90d". Not read when <see cref="From"/> and <see cref="To"/> are set.</summary>
    public string Range { get; set; } = "7d";

    /// <summary>The start of an explicit window. Set together with <see cref="To"/>, or not at all.</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>The end of an explicit window.</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>
    /// <c>previous</c> adds the window of the same length just before this one to a series, for
    /// drawing behind the current line. Read by the series route only, and any other value is
    /// ignored as it was before the parameter existed.
    /// </summary>
    public string? Compare { get; set; }
}

internal sealed class AnalyticsWindowValidator : Validator<AnalyticsWindowRequest>
{
    public AnalyticsWindowValidator() => AnalyticsWindowRules.Apply(this);
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
public sealed class SummaryEndpoint(
    IUmamiClient umami,
    IQuerySession session,
    IConfiguration configuration) : Endpoint<AnalyticsWindowRequest, SummaryResponse>
{
    public override void Configure()
    {
        Get("/api/analytics/{websiteId}/summary");
        Definition.RequireCapability(
            AnalyticsCapabilities.ViewAnalytics, AnalyticsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(AnalyticsWindowRequest req, CancellationToken ct)
    {
        if (!await AnalyticsAccess.AllowedAsync(session, User, configuration, AnalyticsCapabilities.ViewAnalytics, ct))
        {
            ThrowError(PlatformScope.DeploymentWideMessage, 403);
        }

        var (startAt, endAt, _) = AnalyticsRange.Resolve(req.Range, req.From, req.To);
        var s = await umami.GetSummaryAsync(req.WebsiteId, startAt, endAt, ct);
        await Send.OkAsync(new SummaryResponse
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
