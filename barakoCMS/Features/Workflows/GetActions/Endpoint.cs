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
        // Was AllowAnonymous "for testing". This lists every registered action plugin with its
        // required parameters, which tells an anonymous caller exactly which modules an instance
        // runs and how to shape a payload for each. Matches the other workflow endpoints, and the
        // sidebar already restricts Workflows to these two roles.
        Roles("SuperAdmin", "Admin");
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
