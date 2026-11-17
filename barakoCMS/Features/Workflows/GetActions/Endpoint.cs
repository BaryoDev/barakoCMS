using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using FastEndpoints;
using barakoCMS.Models;

namespace barakoCMS.Features.Workflows.GetActions;

/// <summary>
/// Endpoint to list all available workflow action plugins with metadata.
/// </summary>
internal class Endpoint(IWorkflowPluginRegistry registry) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/workflows/actions");
        // Was AllowAnonymous "for testing". This lists every registered action plugin with its
        // required parameters, which tells an anonymous caller exactly which modules an instance
        // runs and how to shape a payload for each. Matches the other workflow endpoints, and the
        // sidebar already restricts Workflows to these two roles.
        Definition.RequireCapability(SystemCapabilities.ManageWorkflows, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.ResponseAsync(registry.GetAllActions(), cancellation: ct);
    }
}
