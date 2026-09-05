using barakoCMS.Modules;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MyBarakoModule;

/// <summary>
/// The ModuleName module for barakoCMS. A host that references the package runs it; a host that
/// names its modules adds it with
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new ModuleNameModule()));</code>
/// </summary>
public sealed class ModuleNameModule : IBarakoModule
{
    public string Name => "ModuleName";

    /// <summary>
    /// The contract this module was written against. Core refuses a module that states a version
    /// it cannot honour, so a breaking contract change fails at startup with this module's name.
    /// </summary>
    public int ContractVersion => ModuleContract.Version;

    /// <summary>
    /// <paramref name="configuration"/> is this module's own section, <c>Modules:ModuleName</c>,
    /// never the application root. Bind it whole, or read keys directly.
    /// </summary>
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ModuleNameOptions>(configuration);
    }

    /// <summary>The document types this module owns. Core refuses a type from another assembly.</summary>
    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<Note>().Index(x => x.CreatedAt);
    }

    /// <summary>
    /// Gives this module's capabilities to the roles that should hold them. Core cannot: it does
    /// not know this module exists. Additive and idempotent, and a role the host never seeded is
    /// skipped rather than created.
    /// </summary>
    public Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct) =>
        ModuleCapabilities.GrantAsync(session, ModuleNameCapabilities.SeededRoles, ModuleNameCapabilities.All, ct);
}
