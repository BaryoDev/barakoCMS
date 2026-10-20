using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using barakoCMS.Modules;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Modules.Configuration;

/// <summary>
/// GET /api/modules/{name}/configuration, the configuration an enabled module runs with and where
/// each value came from. Read only. No secret leaves through it: see
/// <see cref="ModuleConfigurationReader"/> for the order of the checks.
/// </summary>
/// <remarks>
/// A caller who holds <c>view_module_configuration</c> only through a tenant role sees the keys a
/// module marks as tenant scope. Deployment scope keys are left out, not redacted, the same split
/// as the settings routes (<see cref="PlatformScope"/>).
///
/// An unknown module and a disabled one both answer 404 with no body, and the name asked for is
/// never echoed.
/// </remarks>
internal sealed class Endpoint(
    ModuleCatalogue catalogue,
    IQuerySession session,
    IConfiguration configuration) : Endpoint<ModuleConfigurationRequest, ModuleConfigurationResponse>
{
    public override void Configure()
    {
        Get("/api/modules/{name}/configuration");
        Definition.RequireCapability(SystemCapabilities.ViewModuleConfiguration, "SuperAdmin");
        Description(b => b
            .Produces<ModuleConfigurationResponse>(200)
            .Produces(401)
            .Produces(403)
            .Produces(404));
    }

    public override async Task HandleAsync(ModuleConfigurationRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";

        var module = catalogue.Entries.FirstOrDefault(e =>
            e.Enabled && string.Equals(e.Name, req.Name, StringComparison.OrdinalIgnoreCase));
        if (module is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var deployment = await PlatformScope.HoldsGloballyAsync(
            session, User, configuration, SystemCapabilities.ViewModuleConfiguration, ["SuperAdmin"], ct);

        await Send.OkAsync(new ModuleConfigurationReader(configuration).Read(module, deployment), ct);
    }
}
