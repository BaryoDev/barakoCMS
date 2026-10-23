using Marten;
using barakoCMS.Infrastructure.Multitenancy;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>
/// Opens a session for a tenant that is named on the call. Unlike <see cref="TenantSessionFactory"/>
/// it reads no request and holds no scoped state, so a singleton such as a background service can
/// use it, and Wolverine's own singleton session factory is not asked to consume a scoped one.
/// </summary>
public sealed class DurableTenantSessionFactory(IDocumentStore store)
{
    /// <summary>
    /// A session for <paramref name="tenant"/>. The default tenant opens with no tenant id, which is
    /// Marten's default partition, as <see cref="TenantSessionFactory"/> does.
    /// </summary>
    public IDocumentSession Open(string? tenant)
    {
        var slug = TenantScopes.SlugFor(tenant);
        return string.Equals(slug, Models.Tenant.DefaultSlug, StringComparison.Ordinal)
            ? store.LightweightSession()
            : store.LightweightSession(slug);
    }
}
