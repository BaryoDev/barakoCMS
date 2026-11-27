using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace barakoCMS.Modules;

/// <summary>The host's <see cref="IModuleSeedContext"/>, over the module's own session.</summary>
internal sealed class ModuleSeedContext : IModuleSeedContext
{
    internal ModuleSeedContext(IDocumentSession session, IServiceProvider services, string? module = null)
    {
        Module = module;
        Session = session;
        Services = services;
        Reader = new SessionContentReader(session);
    }

    internal IDocumentSession Session { get; }

    /// <summary>The module this context was made for. Null for a context a module made itself, which records nothing.</summary>
    internal string? Module { get; }

    public IServiceProvider Services { get; }

    public IContentReader Reader { get; }

    public IContentWriter Writer => Services.GetRequiredService<IContentWriter>();

    public async Task<bool> EnsureContentTypeAsync(ContentTypeDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // A rollup reads another type and is checked against the stored set, which a seed does not do.
        if (definition.Fields?.Any(f => f?.Rollup is not null) == true)
        {
            throw new InvalidOperationException(
                $"Content type '{definition.Name}' declares a rollup field. A module cannot seed one: add it through the content-type API.");
        }

        var existing = await Session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(t => t.Name == definition.Name, cancellationToken);
        if (existing is null)
        {
            Session.Store(definition);
        }

        await RecordSeededLifecycleAsync(definition, cancellationToken);
        return existing is null;
    }

    /// <summary>
    /// Keeps what the module seeded for the type's lifecycle, on every run and for a type that
    /// already existed, so a lifecycle edit can keep the module's states and transitions. Writing
    /// the same record again stores nothing.
    /// </summary>
    private async Task RecordSeededLifecycleAsync(ContentTypeDefinition definition, CancellationToken ct)
    {
        if (Module is null || definition.Lifecycle is not { } lifecycle)
        {
            return;
        }

        var record = new ModuleSeededType
        {
            Id = ModuleSeededType.KeyOf(definition.Name),
            TypeName = definition.Name,
            Module = Module,
            States = (lifecycle.States ?? []).ToList(),
            InitialState = lifecycle.InitialState,
            Transitions = (lifecycle.Transitions ?? [])
                .Select(t => new SeededTransition { Name = t.Name, From = t.From, To = t.To })
                .ToList(),
        };
        var stored = await Session.LoadAsync<ModuleSeededType>(record.Id, ct);
        if (stored is null || !stored.Same(record))
        {
            Session.Store(record);
        }
    }

    public async Task<bool> EnsureRoleAsync(Guid id, string name, string description, CancellationToken cancellationToken)
    {
        // By id before by name: storing a new role under the seeded id would replace a role an
        // operator renamed, and with it every permission they gave it. By name only among the
        // deployment's roles: a tenant's own role of the same name is not this module's.
        var existing = await Session.LoadAsync<Role>(id, cancellationToken);
        if (existing?.DeletedAt is not null)
        {
            // A deleted seeded role comes back on start, as it did when a delete removed the row.
            Session.Delete(existing);
            existing = null;
        }

        existing ??= await DeploymentRoleNames.FindAsync(Session, name, cancellationToken);
        if (existing is not null)
        {
            return false;
        }

        Session.Store(new Role { Id = id, Name = name, Description = description });
        return true;
    }
}

/// <summary>
/// Builds a seed context over a Marten session, for a module that keeps its older
/// <c>SeedAsync(IDocumentSession, IServiceProvider, CancellationToken)</c> override so a host that
/// only calls that member still seeds it.
/// </summary>
public static class ModuleSeedContexts
{
    /// <summary>The context a module's session-based seed forwards to its context-based seed.</summary>
    [Obsolete("Only for forwarding the older, Marten-typed SeedAsync. Removal planned for barakoCMS 6.0.0.")]
    public static IModuleSeedContext Over(IDocumentSession session, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(services);
        return new ModuleSeedContext(session, services);
    }
}
