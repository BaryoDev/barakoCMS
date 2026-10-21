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
    private readonly List<(Guid Id, object Event, IReadOnlyDictionary<string, object>? Headers)> _streams = new();

    public Npgsql.NpgsqlTransaction? Transaction { get; internal set; }

    private static readonly AsyncLocal<BatchTransaction?> Open = new();

    private readonly List<Func<CancellationToken, Task>> _afterCommit = new();

    /// <summary>Marks this batch as the one running on the current flow, until the scope is disposed.</summary>
    internal IDisposable Running()
    {
        Open.Value = this;
        return new RunningScope();
    }

    private sealed class RunningScope : IDisposable
    {
        public void Dispose() => Open.Value = null;
    }

    /// <summary>
    /// Holds <paramref name="work"/> until the batch's transaction commits when a batch is running on
    /// this flow, and says so. Returns false outside a batch, where the caller does the work now.
    /// </summary>
    /// <remarks>
    /// A listener runs after a flush, which inside a batch is not yet a commit: a reader of another
    /// connection would not find the entry, and a rollback would leave the notice announcing nothing.
    /// </remarks>
    public static bool TryDeferUntilCommit(Func<CancellationToken, Task> work)
    {
        if (Open.Value is not { } batch) return false;
        batch._afterCommit.Add(work);
        return true;
    }

    /// <summary>Runs the work held for the commit, in order. Nothing is run for a batch that rolls back.</summary>
    internal async Task RunAfterCommitAsync(CancellationToken ct)
    {
        var held = _afterCommit.ToList();
        _afterCommit.Clear();
        foreach (var work in held)
        {
            await work(ct);
        }
    }

    /// <summary>
    /// True while an <see cref="EagerStreamsScope"/> is open: new streams start inside the open
    /// transaction instead of waiting for the commit. Off by default and not settable from outside.
    /// </summary>
    public bool EagerStreams { get; private set; }

    /// <summary>
    /// Starts new streams inside the open transaction until the returned scope is disposed. A batch
    /// opens it only around steps that append to an entry the batch created, which needs the stream
    /// to exist. Rollback still undoes the streams, but their sequence numbers are held while it is open.
    /// </summary>
    internal IDisposable EagerStreamsScope()
    {
        EagerStreams = true;
        return new EagerScope(this);
    }

    private sealed class EagerScope(BatchTransaction owner) : IDisposable
    {
        public void Dispose() => owner.EagerStreams = false;
    }

    /// <summary>True inside a batch, where a new stream waits for <see cref="StartDeferredStreams"/>.</summary>
    public bool DefersStreams => Transaction is not null && !EagerStreams;

    public void DeferStream(Guid id, object @event) => _streams.Add((id, @event, null));

    /// <summary>The same, with event headers (the origin sequence) reserved when the entry was written.</summary>
    public void DeferStream(Guid id, object @event, IReadOnlyDictionary<string, object>? headers) =>
        _streams.Add((id, @event, headers));

    /// <summary>Starts every deferred stream on <paramref name="session"/>, in the order they were written.</summary>
    public void StartDeferredStreams(Marten.IDocumentSession session)
    {
        foreach (var (id, @event, headers) in _streams)
            Services.ContentWriter.SetHeaders(session.Events.StartStream<Models.Content>(id, @event).Events, headers);
        _streams.Clear();
    }

    private readonly List<Func<Marten.IDocumentSession, CancellationToken, Task>> _appends = new();

    /// <summary>
    /// Holds a write that appends to an existing stream until the batch commits, for the reason a new
    /// stream waits. Run by <see cref="RunDeferredAppendsAsync"/>, and never for a batch that rolls back.
    /// </summary>
    internal void DeferAppend(Func<Marten.IDocumentSession, CancellationToken, Task> append) => _appends.Add(append);

    /// <summary>Runs every deferred append on <paramref name="session"/>, in the order they were deferred.</summary>
    internal async Task RunDeferredAppendsAsync(Marten.IDocumentSession session, CancellationToken ct)
    {
        foreach (var append in _appends)
        {
            await append(session, ct);
        }

        _appends.Clear();
    }
}
