using Marten;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Opens every injected Marten session for the request's current tenant, so all queries and writes
/// are automatically scoped to it. The default tenant opens sessions with no explicit tenant id,
/// which maps to Marten's default partition — preserving single-tenant deployments unchanged.
/// </summary>
/// <remarks>
/// Inside a content batch (see <see cref="Services.IContentBatchRunner"/>) every session of the scope
/// is enlisted in the batch's one database transaction instead, and does not commit it. Each write
/// in the batch is flushed through that transaction, so the next one's validation and lifecycle
/// hooks read it, and the batch commits or rolls back once at the end.
/// </remarks>
public sealed class TenantSessionFactory : ISessionFactory
{
    private readonly IDocumentStore _store;
    private readonly TenantContext _tenant;
    private readonly BatchTransaction? _batch;

    public TenantSessionFactory(IDocumentStore store, TenantContext tenant)
        : this(store, tenant, null)
    {
    }

    public TenantSessionFactory(IDocumentStore store, TenantContext tenant, BatchTransaction? batch)
    {
        _store = store;
        _tenant = tenant;
        _batch = batch;
    }

    public IQuerySession QuerySession() =>
        _batch?.Transaction is { } tx ? _store.QuerySession(Enlisted(tx))
        : _tenant.IsDefault ? _store.QuerySession() : _store.QuerySession(_tenant.Slug);

    public IDocumentSession OpenSession() =>
        _batch?.Transaction is { } tx ? _store.LightweightSession(Enlisted(tx))
        : _tenant.IsDefault ? _store.LightweightSession() : _store.LightweightSession(_tenant.Slug);

    private Marten.Services.SessionOptions Enlisted(Npgsql.NpgsqlTransaction tx)
    {
        var options = Marten.Services.SessionOptions.ForTransaction(tx);
        if (!_tenant.IsDefault)
            options.TenantId = _tenant.Slug;
        return options;
    }
}

/// <summary>The database transaction a content batch runs in, set for that batch's own scope only.</summary>
public sealed class BatchTransaction
{
    public Npgsql.NpgsqlTransaction? Transaction { get; internal set; }
}
