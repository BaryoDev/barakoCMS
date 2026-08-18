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

    /// <summary>Settings used to live at the root "Umami" section. See IBarakoModule.</summary>
    public string? LegacyConfigurationSection => UmamiOptions.SectionName;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // `configuration` is already this module's own section (Modules:Analytics.Umami).
        services.Configure<UmamiOptions>(configuration);
        services.AddHttpClient<IUmamiClient, UmamiClient>();
    }
}
