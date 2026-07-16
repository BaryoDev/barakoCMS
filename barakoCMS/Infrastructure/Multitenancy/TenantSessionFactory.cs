using Marten;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Opens every injected Marten session for the request's current tenant, so all queries and writes
/// are automatically scoped to it. The default tenant opens sessions with no explicit tenant id,
/// which maps to Marten's default partition — preserving single-tenant deployments unchanged.
/// </summary>
public sealed class TenantSessionFactory : ISessionFactory
{
    private readonly IDocumentStore _store;
    private readonly TenantContext _tenant;

    public TenantSessionFactory(IDocumentStore store, TenantContext tenant)
    {
        _store = store;
        _tenant = tenant;
    }

    public IQuerySession QuerySession() =>
        _tenant.IsDefault ? _store.QuerySession() : _store.QuerySession(_tenant.Slug);

    public IDocumentSession OpenSession() =>
        _tenant.IsDefault ? _store.LightweightSession() : _store.LightweightSession(_tenant.Slug);
}
