using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;

namespace barakoCMS.Features.Monitoring.Kubernetes;

internal class Endpoint(
    IKubernetesMonitorService service,
    ILogger<Endpoint> logger) : EndpointWithoutRequest<ClusterStatus>
{
    public override void Configure()
    {
        Get("/api/monitoring/k8s");
        // Exposes cluster topology (nodes, versions, replica counts), so it is gated like the
        // rest of the monitoring surface rather than left open.
        Definition.RequireCapability(SystemCapabilities.ViewMonitoring, "Admin", "SuperAdmin");
        Description(b => b
            .Produces<ClusterStatus>(200)
            .WithTags("Monitoring"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("Fetching Kubernetes cluster status");
        var status = await service.GetClusterStatusAsync();
        logger.LogInformation("Kubernetes status: IsConnected={IsConnected}, Error={Error}", 
            status.IsConnected, status.Error ?? "None");
        await Send.OkAsync(status, ct);
    }
}
