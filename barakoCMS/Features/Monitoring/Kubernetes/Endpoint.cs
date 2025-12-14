using FastEndpoints;
using barakoCMS.Infrastructure.Services;

namespace barakoCMS.Features.Monitoring.Kubernetes;

public class Endpoint : EndpointWithoutRequest<ClusterStatus>
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
        AllowAnonymous(); // TODO: Restrict to Admin role in production
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
        await SendOkAsync(status, ct);
    }
}
