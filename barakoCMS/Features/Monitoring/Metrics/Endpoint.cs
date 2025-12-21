using FastEndpoints;
using barakoCMS.Infrastructure.Services;

namespace barakoCMS.Features.Monitoring.Metrics;

public class Endpoint : EndpointWithoutRequest<MetricsSummary>
{
    private readonly IMetricsService _metricsService;

    public Endpoint(IMetricsService metricsService)
    {
        _metricsService = metricsService;
    }

    public override void Configure()
    {
        Get("/api/monitoring/metrics");
        Roles("Admin", "SuperAdmin");
        Description(b => b
            .Produces<MetricsSummary>(200)
            .Produces(401)
            .Produces(403)
            .WithTags("Monitoring"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await SendOkAsync(_metricsService.GetSummary(), ct);
    }
}
