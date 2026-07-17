using barakoCMS.Modules;
using Marten;

namespace BarakoCMS.FeatureFlags;

/// <summary>
/// Feature flags for barakoCMS. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new FeatureFlagsModule()));</code>
///
/// Create a flag, toggle it, and target it by club, user, or percentage. Manage flags at
/// <c>/api/feature-flags/admin</c> (Admin/SuperAdmin); read the evaluated set for the current
/// request at <c>GET /api/feature-flags</c>. Inject <see cref="FeatureFlagService"/> for
/// server-side checks. Flags are global (SingleTenanted) with per-tenant targeting.
/// </summary>
public sealed class FeatureFlagsModule : IBarakoModule
{
    public string Name => "FeatureFlags";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<FeatureFlagService>();
    }

    public void ConfigureMarten(StoreOptions options)
    {
        options.Schema.For<FeatureFlag>()
            .SingleTenanted()
            .DocumentAlias("feature_flags")
            .Index(x => x.Key, i => i.IsUnique = true);
    }
}
