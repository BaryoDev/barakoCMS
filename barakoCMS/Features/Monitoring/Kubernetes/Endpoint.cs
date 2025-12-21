using FastEndpoints;
using barakoCMS.Infrastructure.Services;

namespace barakoCMS.Features.Monitoring.Kubernetes;

public class Endpoint : EndpointWithoutRequest<ClusterStatus>
{
    private readonly IKubernetesMonitorService _service;

    public Endpoint(IKubernetesMonitorService service)
    {
        _service = service;
    }

    public override void Configure()
    {
        Get("/api/monitoring/k8s");
        Roles("Admin", "SuperAdmin");
        Description(b => b
            .Produces<ClusterStatus>(200)
            .Produces(403)
            .WithTags("Monitoring"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var status = await _service.GetClusterStatusAsync();
        await SendOkAsync(status, ct);
    }
}
