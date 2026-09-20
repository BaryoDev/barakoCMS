using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;

namespace barakoCMS.Features.Monitoring.Metrics;

internal class Endpoint(IMetricsService metricsService) : EndpointWithoutRequest<MetricsSummary>
{
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
        await Send.OkAsync(metricsService.GetSummary(), ct);
    }
}
