using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Marten.Services;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>
/// FastEndpoints job storage on Marten, with the enqueue riding the request's own session.
/// </summary>
/// <remarks>
/// <see cref="StoreJobAsync"/> receives a record and a token and nothing else, and this class is a
/// singleton, so the caller's session cannot arrive by injection. It arrives through the request
/// scope instead: <see cref="IHttpContextAccessor"/> gives the current request, and its service
/// scope holds the one scoped <see cref="IDocumentSession"/> the endpoint is writing with. The job
/// is staged into that session and commits when the endpoint calls <c>SaveChangesAsync</c>, or not
/// at all. <c>TransactionalEnqueueTests</c> is the proof, both directions.
///
/// That is a property of how the request is written, not of the contract. Two rules follow, and
/// both are on the endpoint: queue through the scoped session you are already writing with, and
/// call <c>SaveChangesAsync</c> afterwards. A request that queues and never saves discards the job,
/// and this class logs a warning when that happens on a successful response so it is at least
/// visible.
///
/// Outside a request there is no scope to share, so the job is written and committed on its own in
/// the default tenant.
///
/// The worker side opens its own sessions per tenant, because a worker has no request. A claim is
/// a load, a lease and a save under Marten's optimistic concurrency, so two instances polling the
/// same table cannot both run one job.
/// </remarks>
internal sealed class MartenJobStorageProvider : IJobStorageProvider<JobRecord>
{
    private readonly IDocumentStore _store;
    private readonly IHttpContextAccessor _http;
    private readonly JobOptions _options;
    private readonly ILogger<MartenJobStorageProvider> _logger;

    /// <summary>
    /// A retry the queue itself planned must not expire before it happens, so the expiry is pushed
    /// past the next attempt by this much when it would otherwise land first.
    /// </summary>
    public static readonly TimeSpan RetryExpiryMargin = TimeSpan.FromHours(1);

    public const int PurgeBatchSize = 500;

    public MartenJobStorageProvider(
        IDocumentStore store, IHttpContextAccessor http, JobOptions options,
        ILogger<MartenJobStorageProvider> logger)
    {
        _store = store;
        _http = http;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Always distributed. barakoCMS runs more than one instance in production, and the alternative
    /// skips the lease check, so two nodes would both run one job.
    /// </summary>
    public bool DistributedJobProcessingEnabled => true;

    public async Task StoreJobAsync(JobRecord r, CancellationToken ct)
    {
        r.CreatedAt = DateTime.UtcNow;
        r.MaxAttempts = _options.MaxAttempts;
        r.State = JobState.Pending;
        r.DequeueAfter = r.ExecuteAfter;

        var http = _http.HttpContext;
        var session = http?.RequestServices.GetService<IDocumentSession>();

        if (http is null || session is null)
        {
            await using var own = _store.LightweightSession();
            r.TenantId = own.TenantId;
            own.Store(r);
            await own.SaveChangesAsync(ct);
            return;
        }

        r.TenantId = session.TenantId;
        session.Store(r);

        // FastEndpoints wakes the worker as soon as this returns, which is before the commit, so
        // that wake finds nothing. This one fires after the commit and finds the job.
        var trigger = new TriggerJobAfterCommit((ICommandBase)r.Command);
        session.Listeners.Add(trigger);
        var origin = Origin(http);

        http.Response.OnCompleted(() =>
        {
            if (!trigger.Committed && http.Response.StatusCode < 400)
            {
                _logger.LogWarning(
                    "Job {TrackingId} ({CommandType}) was queued by {Endpoint} but the request's "
                    + "session never committed, so the job was discarded. Call SaveChangesAsync after queueing.",
                    r.TrackingID, r.CommandType, origin);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Names the endpoint that queued the job by what the application registered (its verbs and
    /// route templates, or its type), never by the request's own method or path, which the caller
    /// chose and could use to forge a log line.
    /// </summary>
    private static string Origin(HttpContext http)
    {
        var endpoint = http.GetEndpoint();
        var definition = endpoint?.Metadata.GetMetadata<EndpointDefinition>();
        if (definition is not null)
        {
            return definition.Verbs is { Length: > 0 } verbs && definition.Routes is { Length: > 0 } routes
                ? $"{string.Join('|', verbs)} {string.Join('|', routes)} ({definition.EndpointType.FullName})"
                : definition.EndpointType.FullName ?? definition.EndpointType.Name;
        }

        return endpoint?.DisplayName ?? "a request outside any endpoint";
    }

    public async Task<ICollection<JobRecord>> GetNextBatchAsync(PendingJobSearchParams<JobRecord> p)
    {
        var ct = p.CancellationToken;
        // The queue's execution limit is Jobs:LeaseSeconds, set in UseBarakoCMS, so the lease and
        // the handler's cancellation expire together. The fallback covers a queue given its own limit.
        var lease = p.ExecutionTimeLimit > TimeSpan.Zero && p.ExecutionTimeLimit != Timeout.InfiniteTimeSpan
            ? p.ExecutionTimeLimit
            : TimeSpan.FromSeconds(_options.LeaseSeconds);

        IReadOnlyList<JobRecord> candidates;
        await using (var query = _store.QuerySession())
        {
            candidates = await query.Query<JobRecord>()
                .Where(p.Match)
                // Every tenant, because a worker serves all of them. Dead letters are never
                // candidates, whatever the queue's own match says.
                .Where(r => r.AnyTenant() && (r.State == JobState.Pending || r.State == JobState.Running))
                .OrderBy(r => r.ExecuteAfter)
                .Take(p.Limit)
                .ToListAsync(ct);
        }

        var stillMatches = p.Match.Compile();
        var claimed = new List<JobRecord>(candidates.Count);

        foreach (var candidate in candidates)
        {
            await using var session = _store.LightweightSession(candidate.TenantId);
            var fresh = await session.LoadAsync<JobRecord>(candidate.TrackingID, ct);
            if (fresh is null || !stillMatches(fresh)
                || fresh.State is JobState.Completed or JobState.DeadLettered)
            {
                continue;
            }

            fresh.State = JobState.Running;
            fresh.DequeueAfter = DateTime.UtcNow + lease;
            session.Store(fresh);

            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (IsConcurrency(ex))
            {
                // Another instance claimed it between the read and the save. Theirs.
                continue;
            }

            claimed.Add(fresh);
        }

        return claimed;
    }

    public async Task MarkJobAsCompleteAsync(JobRecord r, CancellationToken ct)
    {
        await using var session = _store.LightweightSession(r.TenantId);
        var fresh = await session.LoadAsync<JobRecord>(r.TrackingID, ct);
        if (fresh is null) return;

        fresh.IsComplete = true;
        fresh.State = JobState.Completed;
        fresh.CompletedAt = DateTime.UtcNow;
        fresh.NextAttemptAt = null;
        session.Store(fresh);
        await session.SaveChangesAsync(ct);
    }

    public async Task CancelJobAsync(Guid trackingId, CancellationToken ct)
    {
        JobRecord? found;
        await using (var query = _store.QuerySession())
        {
            found = await query.Query<JobRecord>()
                .Where(r => r.AnyTenant() && r.TrackingID == trackingId)
                .FirstOrDefaultAsync(ct);
        }

        if (found is null) return;

        await using var session = _store.LightweightSession(found.TenantId);
        var fresh = await session.LoadAsync<JobRecord>(trackingId, ct);
        if (fresh is null || fresh.IsComplete) return;

        fresh.IsComplete = true;
        fresh.State = JobState.DeadLettered;
        fresh.LastError = "Cancelled.";
        fresh.NextAttemptAt = null;
        fresh.CompletedAt = DateTime.UtcNow;
        session.Store(fresh);
        await session.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Counts the failure, and either schedules the next attempt with backoff or gives up.
    /// </summary>
    /// <remarks>
    /// FastEndpoints calls this until it returns without throwing, so a record that has moved
    /// underneath us is logged and let go rather than rethrown into that loop.
    /// </remarks>
    public async Task OnHandlerExecutionFailureAsync(JobRecord r, Exception exception, CancellationToken ct)
    {
        await using var session = _store.LightweightSession(r.TenantId);
        var fresh = await session.LoadAsync<JobRecord>(r.TrackingID, ct);
        if (fresh is null) return;

        var now = DateTime.UtcNow;
        fresh.AttemptCount++;
        fresh.LastError = Describe(exception);

        if (fresh.AttemptCount >= fresh.MaxAttempts)
        {
            fresh.State = JobState.DeadLettered;
            fresh.NextAttemptAt = null;
            _logger.LogError(
                "Job {TrackingId} ({CommandType}) dead-lettered after {Attempts} attempt(s): {Error}",
                fresh.TrackingID, fresh.CommandType, fresh.AttemptCount, fresh.LastError);
        }
        else
        {
            var next = now + JobBackoff.DelayFor(fresh.AttemptCount, _options.BackoffBaseSeconds, _options.BackoffMaxSeconds);
            fresh.State = JobState.Pending;
            fresh.NextAttemptAt = next;
            fresh.ExecuteAfter = next;
            fresh.DequeueAfter = next;
            if (fresh.ExpireOn < next + RetryExpiryMargin)
                fresh.ExpireOn = next + RetryExpiryMargin;

            _logger.LogWarning(
                "Job {TrackingId} ({CommandType}) failed attempt {Attempt} of {Max}; next at {Next:u}: {Error}",
                fresh.TrackingID, fresh.CommandType, fresh.AttemptCount, fresh.MaxAttempts, next, fresh.LastError);
        }

        session.Store(fresh);

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsConcurrency(ex))
        {
            _logger.LogWarning(ex, "Job {TrackingId} changed while its failure was being recorded; leaving it as is.", r.TrackingID);
        }
    }

    /// <summary>
    /// Hourly. Completed records are deleted. A job that expired without ever completing is
    /// dead-lettered rather than deleted, because an operator has to be able to see it.
    /// </summary>
    public async Task PurgeStaleJobsAsync(StaleJobSearchParams<JobRecord> p)
    {
        var ct = p.CancellationToken;

        IReadOnlyList<JobRecord> stale;
        await using (var query = _store.QuerySession())
        {
            stale = await query.Query<JobRecord>()
                .Where(p.Match)
                .Where(r => r.AnyTenant() && r.State != JobState.DeadLettered)
                .Take(PurgeBatchSize)
                .ToListAsync(ct);
        }

        foreach (var tenant in stale.GroupBy(r => r.TenantId))
        {
            await using var session = _store.LightweightSession(tenant.Key);
            foreach (var record in tenant)
            {
                if (record.IsComplete)
                {
                    session.Delete<JobRecord>(record.TrackingID);
                }
                else
                {
                    record.State = JobState.DeadLettered;
                    record.LastError = "Expired before it ran.";
                    record.NextAttemptAt = null;
                    session.Store(record);
                }
            }

            await session.SaveChangesAsync(ct);
        }
    }

    private static bool IsConcurrency(Exception ex) =>
        ex is JasperFx.ConcurrencyException || ex.GetType().Name.Contains("Concurrency");

    /// <summary>Type and message only. A stack trace is noise here and a response body can hold a credential.</summary>
    private static string Describe(Exception ex) =>
        LogSafe.Text($"{ex.GetType().Name}: {ex.Message}", maxLength: 1000);

    /// <summary>Wakes the command's queue once the session the job was staged in has committed.</summary>
    private sealed class TriggerJobAfterCommit(ICommandBase command) : DocumentSessionListenerBase
    {
        public bool Committed { get; private set; }

        public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
        {
            Committed = true;
            command.TriggerJobExecution();
            return Task.CompletedTask;
        }
    }
}
