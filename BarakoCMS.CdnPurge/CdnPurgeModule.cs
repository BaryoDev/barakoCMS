using barakoCMS.Features.Workflows;
using barakoCMS.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.CdnPurge;

/// <summary>
/// Adds the <c>CdnPurge</c> workflow action. Settings are under <c>Modules:CdnPurge</c>:
/// <c>ZoneId</c> and <c>ApiToken</c>. Without them the action fails the run with a clear reason.
/// </summary>
public sealed class CdnPurgeModule : IBarakoModule
{
    public string Name => "CdnPurge";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CdnPurgeOptions>(configuration);
        services.AddHttpClient<ICdnPurger, CloudflarePurger>();
        services.AddScoped<IWorkflowAction, CdnPurgeAction>();
    }
}
