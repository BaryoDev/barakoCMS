using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace barakoCMS.Modules;

/// <summary>The host's <see cref="IModuleSeedContext"/>, over the module's own session.</summary>
internal sealed class ModuleSeedContext : IModuleSeedContext
{
    internal ModuleSeedContext(IDocumentSession session, IServiceProvider services)
    {
        Session = session;
        Services = services;
        Reader = new SessionContentReader(session);
    }

    internal IDocumentSession Session { get; }

    public IServiceProvider Services { get; }

    public IContentReader Reader { get; }

    public IContentWriter Writer => Services.GetRequiredService<IContentWriter>();

    public async Task<bool> EnsureContentTypeAsync(ContentTypeDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var existing = await Session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(t => t.Name == definition.Name, cancellationToken);
        if (existing is not null)
        {
            return false;
        }

        Session.Store(definition);
        return true;
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

        existing ??= (await Session.Query<Role>()
                               .Where(r => r.DeletedAt == null && r.Name == name && (r.TenantSlug == null || r.TenantSlug == Tenant.DefaultSlug))
                               .ToListAsync(cancellationToken))
                           .OrderBy(r => r.TenantSlug is null ? 0 : 1)
                           .FirstOrDefault();
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
