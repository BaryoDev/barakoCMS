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

    /// <summary>Register the module's services in the container.</summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }

    /// <summary>Register the module's document types / indexes on the shared Marten store.</summary>
    void ConfigureMarten(StoreOptions options) { }

    /// <summary>
    /// Assemblies FastEndpoints should scan for this module's endpoints. Defaults to the module's
    /// own assembly, which is correct when the module type ships alongside its endpoints.
    /// </summary>
    IEnumerable<Assembly> EndpointAssemblies => new[] { GetType().Assembly };

    /// <summary>
    /// Seed idempotent baseline data (roles, reference data). Runs inside a shared session; the
    /// caller commits. Runs only when the host invokes <c>RunBarakoModuleSeedersAsync</c>.
    /// </summary>
    Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
}
