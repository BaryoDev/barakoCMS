using Marten;
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

    /// <summary>
    /// Gives this module's capabilities to the roles that already reached its endpoints.
    /// </summary>
    /// <remarks>
    /// Core cannot do this: <c>SystemCapabilities.DefaultsFor</c> does not know this module exists.
    /// Without it the endpoints would be reachable only through the legacy role-name fallback, and
    /// turning that off, which is the point of issue #443, would take the module away from every
    /// Admin. Additive and idempotent, and it skips a role the host never seeded.
    /// </remarks>
    public Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct) =>
        ModuleCapabilities.GrantAsync(session, AnalyticsCapabilities.SeededRoles, AnalyticsCapabilities.All, ct);
}
