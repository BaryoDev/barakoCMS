using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Modules;

namespace BarakoCMS.Portability;

/// <summary>
/// Content import/export for barakoCMS. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new PortabilityModule()));</code>
///
/// It adds the <c>contents</c> section to the config engine, so entries move with the types:
/// <c>GET /api/config/export?sections=contentTypes,contents</c> downloads them, and
/// <c>POST /api/config/plan</c> and <c>/api/config/apply</c> take them back, with a dry run.
/// Operates within the current club (tenant-scoped content). The module has no routes of its own.
/// </summary>
public sealed class PortabilityModule : IBarakoModule
{
    public string Name => "Portability";

    public int HttpContractVersion => 4;

    public string? PackageId => "BarakoCMS.Portability";

    public string? PackageVersion => ModuleMetadata.VersionOf(GetType());

    public string? Category => ModuleCategory.Platform;

    public string? Description => "Export and import content types and content";

    public string? Glyph => "package";

    /// <summary>Adds the <c>contents</c> section, which is what lets entries move through config export and apply.</summary>
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<barakoCMS.Core.Interfaces.IConfigProvider, ContentsConfigProvider>();

    /// <summary>
    /// Gives this module's capabilities to the Admin role, which the <c>contents</c> section checks.
    /// </summary>
    /// <remarks>
    /// Core cannot do this: <c>SystemCapabilities.DefaultsFor</c> does not know this module exists.
    /// Without it the section would be open to the legacy role-name fallback only, and turning that
    /// off, which is the point of issue #443, would take it away from every Admin. Additive and
    /// idempotent, and it skips a role the host never seeded.
    /// </remarks>
    public Task SeedAsync(IModuleSeedContext context, CancellationToken ct) =>
        PortabilityCapabilities.Defaults.GrantAsync(context, ct);

    [Obsolete("Override SeedAsync(IModuleSeedContext, CancellationToken), which names no Marten type. "
              + "Removal planned for barakoCMS 6.0.0.")]
    public Task SeedAsync(Marten.IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
#pragma warning disable CS0618
        return SeedAsync(ModuleSeedContexts.Over(session, services), ct);
#pragma warning restore CS0618
    }
}
