using barakoCMS.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Analytics.Umami;

/// <summary>
/// Adds Umami web analytics to the barakoCMS admin. Register it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new UmamiAnalyticsModule()));</code>
/// It binds the "Umami" configuration section and registers a typed <see cref="IUmamiClient"/> that
/// proxies a self-hosted Umami instance. The module contributes admin-only endpoints under
/// <c>/api/analytics</c> (from its own assembly) and persists nothing — every read is live from
/// Umami — so it needs no Marten documents.
/// </summary>
public sealed class UmamiAnalyticsModule : IBarakoModule
{
    public string Name => "Analytics.Umami";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<UmamiOptions>(configuration.GetSection(UmamiOptions.SectionName));
        services.AddHttpClient<IUmamiClient, UmamiClient>();
    }
}
