using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;

namespace barakoCMS.Features.Monitoring.Kubernetes;

internal class Endpoint : EndpointWithoutRequest<ClusterStatus>
{
    private readonly IKubernetesMonitorService _service;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(IKubernetesMonitorService service, ILogger<Endpoint> logger)
    {
        _service = service;
        _logger = logger;
    }

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
        _logger.LogInformation("Fetching Kubernetes cluster status");
        var status = await _service.GetClusterStatusAsync();
        _logger.LogInformation("Kubernetes status: IsConnected={IsConnected}, Error={Error}", 
            status.IsConnected, status.Error ?? "None");
        await Send.OkAsync(status, ct);
    }
}
