using FastEndpoints;

namespace BarakoCMS.Analytics.Umami.Features;

public sealed class SiteStatusResponse
{
    /// <summary>True once Umami has received any data for this site — i.e. the snippet is live.</summary>
    public bool Installed { get; set; }
    /// <summary>All-time pageviews (0 = nothing received yet).</summary>
    public long Pageviews { get; set; }
    /// <summary>Visitors active in the last few minutes — the instant "it works" signal after a test visit.</summary>
    public long ActiveNow { get; set; }
    /// <summary>The tracking snippet to paste, so the "not installed" state can show it inline.</summary>
    public string Snippet { get; set; } = "";
}

/// <summary>
/// GET /api/analytics/{websiteId}/status — has this site started sending data? Powers the
/// "add the snippet" instructions and the "verify installation" check in the admin.
/// </summary>
public sealed class StatusEndpoint : Endpoint<AnalyticsWindowRequest, SiteStatusResponse>
{
    private readonly IUmamiClient _umami;

    public StatusEndpoint(IUmamiClient umami) => _umami = umami;

    public override void Configure()
    {
        Get("/api/analytics/{websiteId}/status");
        Roles("Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(AnalyticsWindowRequest req, CancellationToken ct)
    {
        // All-time window: from the Unix epoch to now.
        var endAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var summary = await _umami.GetSummaryAsync(req.WebsiteId, 0, endAt, ct);
        var active = await _umami.GetActiveAsync(req.WebsiteId, ct);

        await SendOkAsync(new SiteStatusResponse
        {
            Pageviews = summary.Pageviews.Value,
            ActiveNow = active,
            Installed = summary.Pageviews.Value > 0 || active > 0,
            Snippet = _umami.TrackingSnippet(req.WebsiteId),
        }, ct);
    }
}
