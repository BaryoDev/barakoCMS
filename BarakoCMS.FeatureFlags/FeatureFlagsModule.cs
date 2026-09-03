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

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<FeatureFlag>()
            .SingleTenanted()
            .DocumentAlias("feature_flags")
            .Index(x => x.Key, i => i.IsUnique = true);
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
        ModuleCapabilities.GrantAsync(session, FeatureFlagCapabilities.SeededRoles, FeatureFlagCapabilities.All, ct);
}
