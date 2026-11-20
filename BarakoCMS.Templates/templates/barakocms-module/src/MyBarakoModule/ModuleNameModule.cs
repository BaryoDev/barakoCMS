using barakoCMS.Models;
using barakoCMS.Modules;
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
    /// Who may create and read notes from the first start. Seeding the type gives no role any right
    /// on it. The host applies this once, only for a role with no entry for the type, so rights an
    /// admin changed later are kept.
    /// </summary>
    public ContentPermissionDefaults? ContentPermissions => ContentPermissionDefaults.None()
        .Allow(ModuleNameContentTypes.Note, SystemRoles.Admin, ContentAction.Create, ContentAction.Read);

    /// <summary>
    /// <paramref name="configuration"/> is this module's own section, <c>Modules:ModuleName</c>,
    /// never the application root. Bind it whole, or read keys directly.
    /// </summary>
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ModuleNameOptions>(configuration);
    }

    /// <summary>
    /// Seeds the note content type and gives this module's capabilities to the roles that should
    /// hold them. Core cannot: it does not know this module exists. Additive and idempotent, so a
    /// restart is not a write, and a role the host never seeded is skipped rather than created.
    /// </summary>
    public async Task SeedAsync(IModuleSeedContext context, CancellationToken ct)
    {
        await context.EnsureContentTypeAsync(ModuleNameContentTypes.NoteDefinition(), ct);
        await ModuleNameCapabilities.Defaults.GrantAsync(context, ct);
    }
}
