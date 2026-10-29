using barakoCMS.Core.Interfaces;
using barakoCMS.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Pages;

/// <summary>
/// A page tree over an existing content type. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new PagesModule()));</code>
/// </summary>
/// <remarks>
/// It stores one document of its own, a tenant's <see cref="PagesSettings"/>. It seeds nothing: creating the page type
/// on boot would make a later <c>blog</c> blueprint apply fail with 409.
/// </remarks>
public sealed class PagesModule : IBarakoModule
{
    public string Name => "Pages";

    public string? PackageId => "BarakoCMS.Pages";

    public string? PackageVersion => ModuleMetadata.VersionOf(GetType());

    public string? Category => ModuleCategory.Content;

    public string? Description => "A page tree with parent and slug rules";

    public string? Glyph => "file-text";

    public int ContractVersion => ModuleContract.Version;

    public int HttpContractVersion => PagesContract.Version;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PagesOptions>()
            .Bind(configuration)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ContentType)
                           && !string.IsNullOrWhiteSpace(o.ParentField)
                           && !string.IsNullOrWhiteSpace(o.ShowInNavigationField)
                           && !string.IsNullOrWhiteSpace(o.OrderField)
                           && !string.IsNullOrWhiteSpace(o.TitleField),
                "Modules:Pages field names may not be empty.")
            .Validate(o => o.MaxDepth >= 1, "Modules:Pages:MaxDepth must be at least 1.")
            .Validate(o => o.MaxPages >= 1, "Modules:Pages:MaxPages must be at least 1.")
            .ValidateOnStart();

        services.AddScoped<IContentLifecycleHook, PageTreeHook>();

        // Every IBlockDataSource a module registered, checked once at start.
        services.AddSingleton<BlockDataSources>();
        services.AddHostedService<BlockDataSourcesCheck>();
    }

    /// <summary>One row per tenant, loaded by its fixed id, so no index.</summary>
    public void ConfigureSchema(IModuleSchema schema) =>
        schema.For<PagesSettings>().DocumentAlias("pages_settings");

    /// <summary>Grants the settings capability its defaults name, as every module seeds its own.</summary>
    public Task SeedAsync(IModuleSeedContext context, CancellationToken ct) =>
        PagesCapabilities.Defaults.GrantAsync(context, ct);

    [Obsolete("Override SeedAsync(IModuleSeedContext, CancellationToken), which names no Marten type. "
              + "Removal planned for barakoCMS 6.0.0.")]
    public Task SeedAsync(Marten.IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
#pragma warning disable CS0618
        return SeedAsync(ModuleSeedContexts.Over(session, services), ct);
#pragma warning restore CS0618
    }
}
