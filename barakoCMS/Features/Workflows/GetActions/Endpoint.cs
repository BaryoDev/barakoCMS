using barakoCMS.Infrastructure.Services;
using FastEndpoints;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.GetActions;

/// <summary>
/// Endpoint to list all available workflow action plugins with metadata.
/// </summary>
public class Endpoint : EndpointWithoutRequest
{
    private readonly IWorkflowPluginRegistry _registry;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(IWorkflowPluginRegistry registry, ILogger<Endpoint> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public override void Configure()
    {
        Get("/api/workflows/actions");
        AllowAnonymous(); // Allow for testing - re-enable auth in production
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            var actions = _registry.GetAllActions();
            await SendAsync(actions, cancellation: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve workflow actions");
            await SendErrorsAsync(cancellation: ct);
        }
    }
}
