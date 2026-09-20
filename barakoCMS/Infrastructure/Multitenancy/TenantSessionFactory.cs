using Marten;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Opens every injected Marten session for the request's current tenant, so all queries and writes
/// are automatically scoped to it. The default tenant opens sessions with no explicit tenant id,
/// which maps to Marten's default partition — preserving single-tenant deployments unchanged.
/// </summary>
public sealed class TenantSessionFactory(IDocumentStore store, TenantContext tenant) : ISessionFactory
{
    public IQuerySession QuerySession() =>
        tenant.IsDefault ? store.QuerySession() : store.QuerySession(tenant.Slug);

    public IDocumentSession OpenSession() =>
        tenant.IsDefault ? store.LightweightSession() : store.LightweightSession(tenant.Slug);
}
