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
/// <remarks>
/// Also holds the batch's content streams until it commits. An event takes its sequence number when
/// it is written, and one written early in a long transaction holds that number uncommitted while
/// other writes commit above it. The async daemon reads that as a gap, gives up on it after its
/// stale threshold, and never processes the event once it does commit, so workflows would silently
/// not run for the imported entries. Documents are written entry by entry, where the next entry's
/// hooks can read them; streams are started together immediately before the commit, and never for a
/// batch that rolls back.
/// </remarks>
public sealed class BatchTransaction
{
    private readonly List<(Guid Id, object Event)> _streams = new();

    public Npgsql.NpgsqlTransaction? Transaction { get; internal set; }

    /// <summary>True inside a batch, where a new stream waits for <see cref="StartDeferredStreams"/>.</summary>
    public bool DefersStreams => Transaction is not null;

    public void DeferStream(Guid id, object @event) => _streams.Add((id, @event));

    /// <summary>Starts every deferred stream on <paramref name="session"/>, in the order they were written.</summary>
    public void StartDeferredStreams(Marten.IDocumentSession session)
    {
        foreach (var (id, @event) in _streams)
            session.Events.StartStream<Models.Content>(id, @event);
        _streams.Clear();
    }
}
