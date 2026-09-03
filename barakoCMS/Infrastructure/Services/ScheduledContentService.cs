using Marten;
using barakoCMS.Models;
using barakoCMS.Events;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Background service that applies scheduled publishing. On each sweep it promotes Drafts whose
/// <see cref="Content.ScheduledPublishAt"/> has arrived to Published, and Archives Published items whose
/// <see cref="Content.ScheduledUnpublishAt"/> has arrived. Each transition emits a ContentStatusChanged
/// event (so the async WorkflowProjection fires "Published" workflows and history stays correct) and
/// clears the consumed schedule field.
///
/// Content is conjoined multi-tenant, so unlike the token cleanup this must sweep every tenant: the
/// default partition plus each active tenant in the registry. Sessions are opened straight off the
/// store with an explicit tenant, since a background service has no request-scoped tenant.
/// </summary>
public class ScheduledContentService : BackgroundService
{
    private readonly IDocumentStore _store;
    private readonly ILogger<ScheduledContentService> _logger;

    // A minute is fine granularity for editorial scheduling and keeps the query load trivial (a couple
    // of indexed lookups per tenant per minute).
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    // Transitions the scheduler makes are attributed to the system, not a user.
    public static readonly Guid SystemActor = Guid.Empty;

    public ScheduledContentService(IDocumentStore store, ILogger<ScheduledContentService> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scheduled content service started. Sweep interval: {Interval}", SweepInterval);

        // Let the app (and the Marten schema/daemon) warm up before the first sweep.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAllTenantsAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scheduled content sweep");
            }

            await Task.Delay(SweepInterval, stoppingToken);
        }

        _logger.LogInformation("Scheduled content service stopped");
    }

    /// <summary>
    /// A Postgres advisory lock key, arbitrary but fixed, identifying this sweep across instances.
    /// </summary>
    /// <remarks>
    /// Changing it would let an old and a new deployment sweep simultaneously during a rollout, which
    /// is the exact situation the lock exists for.
    /// </remarks>
    private const long SweepLockKey = 8_242_026_001L;

    /// <summary>
    /// Sweeps the default partition and every active tenant.
    /// </summary>
    /// <remarks>
    /// The signature this shipped with. Kept because barakoCMS is a package and this class is public,
    /// and a return type cannot be overloaded, so the answer-carrying version needed its own name.
    /// </remarks>
    public Task SweepAllTenantsAsync(DateTime nowUtc, CancellationToken ct) =>
        TrySweepAllTenantsAsync(nowUtc, ct);

    /// <summary>Sweeps the default partition and every active tenant. Exposed for tests.</summary>
    /// <returns>
    /// True if this instance held the lock and swept; false if another instance was already sweeping
    /// and this tick did nothing.
    /// </returns>
    /// <remarks>
    /// This runs on every node, unlike the projection daemon, because a BackgroundService has no
    /// leader election of its own. Without the lock two instances both query for due content, both
    /// append ContentStatusChanged, and the item transitions twice: two events on the stream, and
    /// the workflow projection firing every "Published" workflow twice, which for an email or a
    /// webhook action means the recipient gets two.
    ///
    /// A session-scoped advisory lock rather than a transaction-scoped one, because the sweep opens
    /// a separate session per tenant and the lock has to outlive all of them. Held on a connection
    /// kept open for the duration and released in the finally, so a crash mid-sweep frees it when
    /// the connection drops rather than wedging every future tick.
    /// </remarks>
    public async Task<bool> TrySweepAllTenantsAsync(DateTime nowUtc, CancellationToken ct)
    {
        // The connection object, not a new one built from its ConnectionString: Npgsql redacts the
        // password out of ConnectionString unless Persist Security Info is set, so rebuilding from it
        // fails authentication.
        await using var lockConnection = _store.Storage.Database.CreateConnection();
        await lockConnection.OpenAsync(ct);

        await using (var acquire = lockConnection.CreateCommand())
        {
            acquire.CommandText = "select pg_try_advisory_lock(@key)";
            acquire.Parameters.AddWithValue("key", SweepLockKey);
            var acquired = (bool?)await acquire.ExecuteScalarAsync(ct) ?? false;
            if (!acquired)
            {
                _logger.LogDebug("Another instance is sweeping scheduled content; skipping this tick.");
                return false;
            }
        }

        try
        {
            await SweepHeldAsync(nowUtc, ct);
            return true;
        }
        finally
        {
            await using var release = lockConnection.CreateCommand();
            release.CommandText = "select pg_advisory_unlock(@key)";
            release.Parameters.AddWithValue("key", SweepLockKey);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private async Task SweepHeldAsync(DateTime nowUtc, CancellationToken ct)
    {
        // null slug => the default (no-explicit-tenant) partition, where single-deployment sites like
        // baryo.dev keep their content; named slugs are the path-based tenants.
        var partitions = new List<string?> { null };
        await using (var query = _store.QuerySession())
        {
            var tenants = await query.Query<Tenant>().Where(t => t.IsActive).ToListAsync(ct);
            partitions.AddRange(tenants.Select(t => (string?)t.Slug));
        }

        foreach (var slug in partitions.Distinct())
        {
            await using var session = slug is null ? _store.LightweightSession() : _store.LightweightSession(slug);
            var changed = await SweepTenantAsync(session, nowUtc, _logger, ct);
            if (changed > 0)
                _logger.LogInformation("Scheduled sweep applied {Count} transition(s) for tenant {Tenant}",
                    changed, slug ?? "(default)");
        }
    }

    /// <summary>How many due items one query loads, and one transaction commits.</summary>
    public const int DefaultBatchSize = 200;

    /// <summary>
    /// How many batches one sweep will take before leaving the rest to the next tick, a minute later.
    /// </summary>
    public const int DefaultMaxBatchesPerSweep = 25;

    /// <summary>
    /// Applies all due transitions in one tenant session and saves. Returns the number of items flipped.
    /// Pure over the session so tests can drive it directly without the timer.
    /// </summary>
    /// <remarks>
    /// The signature this shipped with, kept for callers compiled against it. Skipped items are
    /// invisible through this overload; pass a logger to see them.
    /// </remarks>
    public static Task<int> SweepTenantAsync(IDocumentSession session, DateTime nowUtc, CancellationToken ct) =>
        SweepTenantAsync(session, nowUtc, null, DefaultBatchSize, DefaultMaxBatchesPerSweep, ct);

    public static Task<int> SweepTenantAsync(
        IDocumentSession session, DateTime nowUtc, ILogger? logger, CancellationToken ct) =>
        SweepTenantAsync(session, nowUtc, logger, DefaultBatchSize, DefaultMaxBatchesPerSweep, ct);

    /// <summary>
    /// Applies due transitions in batches, saving each item on its own, until nothing is due or
    /// <paramref name="maxBatches"/> is reached. Returns the number flipped, which is not
    /// necessarily the number that were due: an item another writer changed underneath the sweep is
    /// left for the next tick.
    /// </summary>
    /// <remarks>
    /// Two guards that arrived separately and both belong here.
    ///
    /// The query used to have no limit, so the sweep's memory was however much had accumulated:
    /// normally nothing, but after downtime or a bulk import with schedules it is the whole backlog
    /// in one list and one transaction. Batching makes the worst case a property of the code rather
    /// than of how long the service was off (#127).
    ///
    /// The save used to be one commit for the whole batch with no version check, so the sweep loaded
    /// a document, an editor committed against the same content, and storing the loaded copy
    /// reverted the editor's data with no event recording it. The stream then disagreed with the
    /// read model permanently. It is one save per item under an expected-version check now, and an
    /// item that conflicts is skipped rather than retried, because its schedule is still armed and
    /// the next tick a minute later picks it up against fresh state (#299).
    ///
    /// The cap is what guarantees the loop ends, and it matters more now than it did. A drained
    /// sweep is still the normal outcome, but items that keep losing to a concurrent writer stay in
    /// the predicate, so the same batch can come back. The cap bounds that, and a sweep holds the
    /// cross-instance advisory lock and must not be able to hold it forever.
    /// </remarks>
    public static Task<int> SweepTenantAsync(
        IDocumentSession session, DateTime nowUtc, ILogger? logger, int batchSize, int maxBatches,
        CancellationToken ct) =>
        SweepTenantAsync(session, nowUtc, logger, batchSize, maxBatches, beforeSave: null, ct);

    /// <summary>
    /// The implementation, with a hook that runs after an item is loaded and before its save.
    /// </summary>
    /// <remarks>
    /// The hook exists for one test and has no production caller: every public overload passes null.
    /// It fires between the append and the commit, because that is the window AppendOptimistic
    /// guards. An edit that commits before the append is not a conflict at all: the writer rebuilds
    /// the document from what is committed at that point, so the sweep simply picks up fresh state.
    ///
    /// It is here because the guard above cannot otherwise be proved. The test that covered it
    /// started a sweep and an edit with Task.WhenAll and asserted the document agreed with the
    /// stream, which holds trivially whenever the two do not actually overlap. Deleting the
    /// expected-version append left that test green, which is the same as having no test. The sweep
    /// constructs its own writer, so a decorator cannot reach in, and this is the smallest seam that
    /// makes the collision certain rather than likely. See #393.
    /// </remarks>
    internal static async Task<int> SweepTenantAsync(
        IDocumentSession session, DateTime nowUtc, ILogger? logger, int batchSize, int maxBatches,
        Func<Content, CancellationToken, Task>? beforeSave, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatches, 1);

        var applied = 0;

        for (var batch = 0; batch < maxBatches; batch++)
        {
            var due = await session.Query<Content>()
                // Scheduled, not Draft. Arming a publish time moves the entry to Scheduled now that
                // it is a real status, so a Draft with a date on it is either pre-4.0 data the
                // migration missed or something wrote the document without going through the
                // schedule endpoint. Both are still swept, because leaving them would mean a
                // publish time that silently never fires.
                .Where(c => ((c.Status == ContentStatus.Scheduled || c.Status == ContentStatus.Draft)
                             && c.ScheduledPublishAt != null && c.ScheduledPublishAt <= nowUtc)
                         || (c.Status == ContentStatus.Published
                             && c.ScheduledUnpublishAt != null && c.ScheduledUnpublishAt <= nowUtc))
                .OrderBy(c => c.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (due.Count == 0) break;

            // Constructed rather than injected: this sweep opens its own session per tenant, so there
            // is no scoped writer to resolve.
            var writer = new ContentWriter(session, new ContentSourcingPolicyService(session));
            var appliedInBatch = 0;

            foreach (var content in due)
            {
                var newStatus = content.Status == ContentStatus.Published
                    ? ContentStatus.Archived
                    : ContentStatus.Published;

                var events = new object[]
                {
                    new ContentStatusChanged(content.Id, newStatus, SystemActor, DateTime.UtcNow),

                    // Clear only the field just consumed; the opposite one stays armed, since a
                    // Published item can still carry a future unpublish time. Recorded as an event
                    // rather than written straight to the document: consuming a schedule is a state
                    // change, and one that happened without a user, so the trail is the only place
                    // it is visible.
                    newStatus == ContentStatus.Published
                        ? new ContentScheduled(content.Id, null, content.ScheduledUnpublishAt, SystemActor, DateTime.UtcNow)
                        : new ContentScheduled(content.Id, content.ScheduledPublishAt, null, SystemActor, DateTime.UtcNow),
                };

                try
                {
                    if (await session.Events.FetchStreamStateAsync(content.Id, ct) is null)
                    {
                        // A document with no stream behind it: seeded demo rows, and anything written
                        // before every write went through the writer. There is no version to check, so
                        // there is nothing for an expected-version append to guard, and demanding one
                        // would leave the item throwing on every tick forever.
                        foreach (var @event in events)
                        {
                            await writer.AppendAsync(content, @event, ct);
                        }
                    }
                    else
                    {
                        await writer.AppendOptimisticAsync(content, events, ct);
                    }

                    // Between the append and the commit, which is the window AppendOptimistic
                    // guards and therefore the only interleaving that can make this save lose.
                    if (beforeSave is not null)
                    {
                        await beforeSave(content, ct);
                    }

                    await session.SaveChangesAsync(ct);
                    applied++;
                    appliedInBatch++;
                }
                catch (Exception ex) when (ex is JasperFx.ConcurrencyException
                    || ex.GetType().Name.Contains("Concurrency")
                    || ex.GetType().Name.Contains("UnexpectedMaxEventId"))
                {
                    // Nothing of this item's is left staged, or the next item's save would carry it
                    // and fail for the same reason.
                    session.EjectAllPendingChanges();

                    logger?.LogInformation(
                        "Scheduled transition for {ContentId} was overtaken by another writer; leaving it for the next sweep",
                        content.Id);
                }
            }

            // A short batch means the backlog is drained. A full batch that applied nothing means
            // every item in it lost its race, and re-querying would return the same ones, so stop
            // rather than spend the remaining batches on them.
            if (due.Count < batchSize || appliedInBatch == 0) break;
        }

        return applied;
    }
}
