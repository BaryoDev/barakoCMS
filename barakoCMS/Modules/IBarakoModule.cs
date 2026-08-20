using System.Reflection;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace barakoCMS.Modules;

/// <summary>
/// An optional, self-contained feature module layered on top of barakoCMS core (e.g. accounting,
/// CRM, events). A host opts in by registering modules when calling <c>AddBarakoCMS</c>; core stays
/// generic and knows nothing about any particular module.
///
/// A module can contribute DI services, its own strongly-typed Marten documents, FastEndpoints
/// endpoints (in its own assembly), and seed data — implementing only the hooks it needs, since all
/// but <see cref="Name"/> have default no-op implementations.
/// </summary>
public interface IBarakoModule
{
    /// <summary>Stable identifier for logging/diagnostics, e.g. "Accounting".</summary>
    string Name { get; }

    /// <summary>
    /// Which version of the module contract this module was written against. See
    /// <see cref="ModuleContract"/> for what the contract covers and what moves the number.
    /// </summary>
    /// <remarks>
    /// Declare it:
    /// <code>public int ContractVersion => ModuleContract.Version;</code>
    ///
    /// The default is <c>0</c>, meaning unstated, and unstated is accepted. A first-party module in
    /// this repository is compiled against the same core it runs on, so the number can only tell a
    /// third-party module apart from the core it was built for, and refusing modules for not
    /// answering a question core only started asking in 3.21 would break every module that exists.
    ///
    /// Core logs which modules stated a version and which did not, and refuses one that states a
    /// version it cannot support. That is the whole enforcement, deliberately: the point is that an
    /// author can find out they are on the wrong side of a change, not that core polices them.
    /// </remarks>
    int ContractVersion => 0;

    /// <summary>
    /// Names of modules that must be configured before this one.
    /// </summary>
    /// <remarks>
    /// Registration order decides who configures services first, and therefore who wins when two
    /// modules touch the same registration. That order is currently whatever the host wrote by
    /// hand, and assembly discovery does not have one to offer.
    ///
    /// Declaring the dependency makes the requirement explicit and enforced: modules are sorted
    /// before anything runs, a missing dependency is refused by name, and a cycle is refused with
    /// the cycle printed. Modules that do not depend on each other keep their declared order, so a
    /// build stays reproducible.
    ///
    /// Only ordering. It does not register the dependency for you, and it does not let you reach
    /// into another module's services.
    /// </remarks>
    IEnumerable<string> DependsOn => Array.Empty<string>();

    /// <summary>
    /// Register the module's services in the container.
    /// </summary>
    /// <param name="configuration">
    /// The module's OWN configuration section, <c>Modules:{Name}</c>, not the application root.
    /// A module receives only its own settings: the root also holds the database connection string,
    /// the JWT signing key and the seeded admin credentials, and no module needs any of them.
    /// Read keys directly (<c>configuration["ApiKey"]</c>) or bind the whole section
    /// (<c>services.Configure&lt;MyOptions&gt;(configuration)</c>).
    /// </param>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }

    /// <summary>
    /// A root-level configuration section this module used to read, for modules that predate the
    /// move to scoped configuration. When <c>Modules:{Name}</c> holds nothing and this section
    /// does, the host passes the legacy section instead and logs a warning naming both.
    ///
    /// Transitional. Without it, upgrading silently un-configures a module: the app starts, the
    /// module registers, and it quietly behaves as though it was never set up. Set it to null once
    /// deployments have moved.
    /// </summary>
    string? LegacyConfigurationSection => null;

    /// <summary>
    /// Register the module's own document types and indexes.
    /// </summary>
    /// <remarks>
    /// <paramref name="schema"/> accepts only types from assemblies this module ships. Everything
    /// chains as before; only the entry point changed:
    /// <code>
    /// schema.For&lt;MyDocument&gt;().Index(x =&gt; x.SomeField);
    /// </code>
    /// </remarks>
    void ConfigureSchema(IModuleSchema schema) { }

    /// <summary>
    /// Register document types directly on the shared Marten store.
    /// </summary>
    /// <remarks>
    /// Superseded by <see cref="ConfigureSchema"/>. This receives the same <see cref="StoreOptions"/>
    /// core configured, so it can re-map core documents, change tenancy or alter the event store.
    /// That was never intended and is not something a module needs.
    ///
    /// Still called, so existing modules keep working, and the host logs a warning naming any module
    /// that overrides it.
    /// </remarks>
    [Obsolete("Use ConfigureSchema(IModuleSchema), which restricts a module to its own document types. "
              + "ConfigureMarten will be removed in barakoCMS 5.0.")]
    void ConfigureMarten(StoreOptions options) { }

    /// <summary>
    /// Assemblies FastEndpoints should scan for this module's endpoints. Defaults to the module's
    /// own assembly, which is correct when the module type ships alongside its endpoints.
    /// </summary>
    IEnumerable<Assembly> EndpointAssemblies => new[] { GetType().Assembly };

    /// <summary>
    /// Assemblies whose document types this module may configure through <see cref="ConfigureSchema"/>.
    /// Defaults to the module's own assembly.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="EndpointAssemblies"/> on purpose. Sharing one list means widening
    /// endpoint scanning also widens what a module may configure, so listing <c>barakoCMS</c> to have
    /// its endpoints scanned would grant permission to re-map core's documents. Two declarations, two
    /// questions: where are my endpoints, and what data do I own.
    ///
    /// Override this only for a module that ships its document types in a separate assembly, which
    /// the endpoint list would not cover because that assembly contains no endpoints.
    /// </remarks>
    IEnumerable<Assembly> SchemaAssemblies => new[] { GetType().Assembly };

    /// <summary>
    /// Seed idempotent baseline data (roles, reference data). Runs only when the host invokes
    /// <c>RunBarakoModuleSeedersAsync</c>.
    ///
    /// The session is yours alone and the host commits it. Do not call <c>SaveChangesAsync</c>
    /// yourself: the host does it once your seed returns, and committing early gives up the
    /// all-or-nothing property your own seed relies on.
    ///
    /// You cannot see another module's seed data here, committed or not, and it cannot see yours.
    /// <see cref="DependsOn"/> does order seeding as well as configuration, but the sessions are
    /// isolated, so a module that ran earlier has committed data you still cannot read from here.
    /// Seed only what your own module owns.
    ///
    /// Throwing fails your module's seed and nobody else's. It is logged against your module name
    /// and rethrown to the host once every module has had its turn.
    /// </summary>
    Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
}
