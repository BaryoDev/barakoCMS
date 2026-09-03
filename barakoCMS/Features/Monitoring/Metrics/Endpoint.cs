using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;

namespace barakoCMS.Features.Monitoring.Metrics;

internal class Endpoint : EndpointWithoutRequest<MetricsSummary>
{
    private readonly IMetricsService _metricsService;

    public Endpoint(IMetricsService metricsService)
    {
        _metricsService = metricsService;
    }

    public override void Configure()
    {
        Get("/api/monitoring/metrics");
        Definition.RequireCapability(SystemCapabilities.ViewMonitoring, "Admin", "SuperAdmin");
        Description(b => b
            .Produces<MetricsSummary>(200)
            .Produces(401)
            .Produces(403)
            .WithTags("Monitoring"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(_metricsService.GetSummary(), ct);
    }
}
