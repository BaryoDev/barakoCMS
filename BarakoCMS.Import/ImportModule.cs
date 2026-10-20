using Marten;
using barakoCMS.Modules;

namespace BarakoCMS.Import;

/// <summary>
/// Optional bulk-import module for barakoCMS. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new ImportModule()));</code>
/// It contributes two endpoints: one analyzes an uploaded .xlsx/CSV into a preview grid, the other
/// bulk-creates content from mapped records. It has no document types of its own, and parsing is
/// delegated to the zero-dependency Talaan library.
/// </summary>
public sealed class ImportModule : IBarakoModule
{
    public string Name => "Import";

    public string? PackageId => "BarakoCMS.Import";

    public string? PackageVersion => ModuleMetadata.VersionOf(GetType());

    public string? Category => ModuleCategory.Content;

    public string? Description => "Bulk-create content from xlsx or CSV";

    public string? Glyph => "upload";
    // Default hooks: no services and no Marten docs. Endpoints live in this assembly and are
    // discovered via IBarakoModule.EndpointAssemblies (defaulting to this assembly).

    /// <summary>
    /// Gives this module's capability to the roles the import tool was meant for.
    /// </summary>
    /// <remarks>
    /// Core cannot do this: <c>SystemCapabilities.DefaultsFor</c> does not know this module exists.
    /// Additive and idempotent, and it skips a role the host never seeded.
    /// </remarks>
    public Task SeedAsync(IModuleSeedContext context, CancellationToken ct) =>
        ImportCapabilities.Defaults.GrantAsync(context, ct);

    [Obsolete("Override SeedAsync(IModuleSeedContext, CancellationToken), which names no Marten type. "
              + "Removal planned for barakoCMS 6.0.0.")]
    public Task SeedAsync(Marten.IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
#pragma warning disable CS0618
        return SeedAsync(ModuleSeedContexts.Over(session, services), ct);
#pragma warning restore CS0618
    }
}
