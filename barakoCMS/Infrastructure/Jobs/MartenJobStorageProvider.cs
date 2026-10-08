using System.Diagnostics;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Tracing;
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
    private readonly JobStorageGate _gate;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// A retry the queue itself planned must not expire before it happens, so the expiry is pushed
    /// past the next attempt by this much when it would otherwise land first.
    /// </summary>
    public static readonly TimeSpan RetryExpiryMargin = TimeSpan.FromHours(1);

    public const int PurgeBatchSize = 500;

    /// <summary>How long one count for the queue gauges may take before it is given up.</summary>
    internal static readonly TimeSpan MeasureBudget = TimeSpan.FromSeconds(10);

    /// <summary>Where this provider counts what it does. A test gives it a registry of its own.</summary>
    internal JobMetrics Metrics { get; init; } = JobMetrics.Default;

    /// <summary>The run spans of the jobs this provider claimed and has not finished.</summary>
    internal JobRunSpans RunSpans { get; } = new();

    private long _measuredAt;
    private int _measuring;

    public MartenJobStorageProvider(
        IDocumentStore store, IHttpContextAccessor http, JobOptions options,
        ILogger<MartenJobStorageProvider> logger, JobStorageGate gate, IConfiguration configuration)
    {
        _store = store;
        _http = http;
        _options = options;
        _logger = logger;
        _gate = gate;
        _configuration = configuration;
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
        await _gate.WaitAsync(ct);
        // The queue's execution limit is Jobs:LeaseSeconds, set in UseBarakoCMS, so the lease and
        // the handler's cancellation expire together. The fallback covers a queue given its own limit.
        var lease = p.ExecutionTimeLimit > TimeSpan.Zero && p.ExecutionTimeLimit != Timeout.InfiniteTimeSpan
            ? p.ExecutionTimeLimit
            : TimeSpan.FromSeconds(_options.LeaseSeconds);

        IReadOnlyList<JobRecord> candidates;
        if (TenantPartitions.Enforced(_configuration))
        {
            // Each partition's top Limit, merged and cut again, is the global top Limit, so no
            // early stop here: a later tenant may hold the oldest job.
            candidates = (await PerTenantAsync((q, _) => q
                    .Where(p.Match)
                    .Where(r => r.State == JobState.Pending || r.State == JobState.Running)
                    .OrderBy(r => r.ExecuteAfter)
                    .Take(p.Limit), stopAt: null, ct))
                .OrderBy(r => r.ExecuteAfter)
                .Take(p.Limit)
                .ToList();
        }
        else
        {
            await using var query = _store.QuerySession();
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

        // Only a poll that found something gets a span. The queue polls far more often than it
        // finds work, and an empty poll is nothing anyone needs to see in a trace.
        Activity? claim = null;
        if (candidates.Count > 0)
        {
            RunSpans.EndStartedBefore(DateTime.UtcNow - lease - lease);
            claim = BarakoTracing.StartJobClaim(p.QueueID);
        }

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
            if (claim is not null) RunSpans.Started(fresh, claim.Context);
        }

        BarakoTracing.RecordClaimed(claim, claimed.Count);
        claim?.Dispose();

        StartMeasureWhenDue(ct);

        return claimed;
    }

    /// <summary>
    /// Starts a count of the queue for the gauges, at most once every
    /// <see cref="JobOptions.MetricsIntervalSeconds"/> across every queue's poll on this node, and
    /// never from a scrape.
    /// </summary>
    /// <remarks>
    /// Not awaited. The jobs just claimed are leased from now, so the count must not hold them back,
    /// and it can take up to <see cref="MeasureBudget"/>. One count runs at a time; the token is the
    /// poll's, so a stopping worker ends it.
    /// </remarks>
    private void StartMeasureWhenDue(CancellationToken ct)
    {
        if (_options.MetricsIntervalSeconds <= 0) return;

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _measuredAt);
        if (last != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromSeconds(_options.MetricsIntervalSeconds))
            return;
        if (Interlocked.CompareExchange(ref _measuredAt, now, last) != last) return;
        if (Interlocked.Exchange(ref _measuring, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await MeasureAsync(ct);
            }
            finally
            {
                Volatile.Write(ref _measuring, 0);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Counts the due jobs, finds the one due longest, and counts the dead letters, then publishes
    /// all three. Never throws.
    /// </summary>
    /// <remarks>
    /// Two counts and one read of one timestamp, per partition when Postgres enforces the tenant
    /// filter and once otherwise. Nothing is loaded into memory. Given up after
    /// <see cref="MeasureBudget"/>, and then nothing is published: part of a count would read as a
    /// queue that shrank, so the gauges keep their last numbers.
    /// </remarks>
    /// <returns>Whether new numbers were published.</returns>
    internal async Task<bool> MeasureAsync(CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(MeasureBudget);

        try
        {
            var now = DateTime.UtcNow;
            var due = 0;
            var deadLettered = 0;
            DateTime? oldest = null;

            if (TenantPartitions.Enforced(_configuration))
            {
                foreach (var tenantId in await TenantPartitions.FromRegistryAsync(_store, budget.Token))
                {
                    await using var query = _store.QuerySession(tenantId);
                    var jobs = query.Query<JobRecord>();

                    deadLettered += await jobs.CountAsync(r => r.State == JobState.DeadLettered, budget.Token);

                    var waiting = await jobs.CountAsync(r => (r.State == JobState.Pending || r.State == JobState.Running)
                        && r.ExecuteAfter <= now && r.DequeueAfter <= now, budget.Token);
                    if (waiting == 0) continue;

                    due += waiting;
                    var first = await jobs
                        .Where(r => (r.State == JobState.Pending || r.State == JobState.Running)
                            && r.ExecuteAfter <= now && r.DequeueAfter <= now)
                        .OrderBy(r => r.ExecuteAfter)
                        .Select(r => r.ExecuteAfter)
                        .Take(1)
                        .ToListAsync(budget.Token);
                    if (first.Count > 0 && (oldest is null || first[0] < oldest)) oldest = first[0];
                }
            }
            else
            {
                await using var query = _store.QuerySession();
                var jobs = query.Query<JobRecord>();

                deadLettered = await jobs.CountAsync(r => r.AnyTenant() && r.State == JobState.DeadLettered, budget.Token);
                due = await jobs.CountAsync(r => r.AnyTenant()
                    && (r.State == JobState.Pending || r.State == JobState.Running)
                    && r.ExecuteAfter <= now && r.DequeueAfter <= now, budget.Token);

                if (due > 0)
                {
                    var first = await jobs
                        .Where(r => r.AnyTenant()
                            && (r.State == JobState.Pending || r.State == JobState.Running)
                            && r.ExecuteAfter <= now && r.DequeueAfter <= now)
                        .OrderBy(r => r.ExecuteAfter)
                        .Select(r => r.ExecuteAfter)
                        .Take(1)
                        .ToListAsync(budget.Token);
                    if (first.Count > 0) oldest = first[0];
                }
            }

            Metrics.Queue(due, oldest is { } dueAt ? now - dueAt : TimeSpan.Zero, deadLettered);
            return true;
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                // The type only: a message can quote what the database was asked.
                _logger.LogWarning(
                    "The job queue could not be counted for its gauges ({Reason}); they keep their last numbers",
                    budget.IsCancellationRequested ? $"it took longer than {MeasureBudget.TotalSeconds:0} seconds" : ex.GetType().Name);
            }

            return false;
        }
    }

    public Task MarkJobAsCompleteAsync(JobRecord r, CancellationToken ct) =>
        FinishAsync(r, () => CompleteAsync(r, ct));

    /// <summary>
    /// Runs the write that records how a job went under a span of its own, then ends the job's run
    /// span with the outcome. A write that throws ends it as <see cref="BarakoTracing.JobError"/>.
    /// </summary>
    private async Task FinishAsync(JobRecord r, Func<Task<string>> record)
    {
        var run = RunSpans.Take(r.TrackingID);
        var outcome = BarakoTracing.JobError;

        try
        {
            using var finish = BarakoTracing.StartJobFinish(run);
            outcome = await record();
        }
        finally
        {
            BarakoTracing.EndJobRun(run, outcome);
        }
    }

    private async Task<string> CompleteAsync(JobRecord r, CancellationToken ct)
    {
        await using var session = _store.LightweightSession(r.TenantId);
        var fresh = await session.LoadAsync<JobRecord>(r.TrackingID, ct);
        if (fresh is null) return BarakoTracing.JobGone;

        fresh.IsComplete = true;
        fresh.State = JobState.Completed;
        fresh.CompletedAt = DateTime.UtcNow;
        fresh.NextAttemptAt = null;
        session.Store(fresh);
        await session.SaveChangesAsync(ct);

        Metrics.Recorded(JobMetrics.Succeeded);
        return JobMetrics.Succeeded;
    }

    public async Task CancelJobAsync(Guid trackingId, CancellationToken ct)
    {
        JobRecord? found;
        if (TenantPartitions.Enforced(_configuration))
        {
            found = (await PerTenantAsync((q, _) => q.Where(r => r.TrackingID == trackingId).Take(1), stopAt: 1, ct))
                .FirstOrDefault();
        }
        else
        {
            await using var query = _store.QuerySession();
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
    public Task OnHandlerExecutionFailureAsync(JobRecord r, Exception exception, CancellationToken ct) =>
        FinishAsync(r, () => RecordFailureAsync(r, exception, ct));

    private async Task<string> RecordFailureAsync(JobRecord r, Exception exception, CancellationToken ct)
    {
        await using var session = _store.LightweightSession(r.TenantId);
        var fresh = await session.LoadAsync<JobRecord>(r.TrackingID, ct);
        if (fresh is null) return BarakoTracing.JobGone;

        var now = DateTime.UtcNow;
        fresh.AttemptCount++;
        fresh.LastError = Describe(exception);

        var deadLettered = fresh.AttemptCount >= fresh.MaxAttempts;
        if (deadLettered)
        {
            // When it gave up, which is what Jobs:DeadLetterRetentionDays counts from.
            fresh.State = JobState.DeadLettered;
            fresh.NextAttemptAt = null;
            fresh.CompletedAt = now;
            _logger.LogError(
                "Job {TrackingId} ({CommandType}) dead-lettered after {Attempts} attempt(s): {Error}",
                fresh.TrackingID, fresh.CommandType, fresh.AttemptCount, fresh.LastError);
        }
        else
        {
            var next = now + JobBackoff.DelayFor(
                fresh.AttemptCount, _options.BackoffBaseSeconds, _options.BackoffMaxSeconds, Random.Shared);
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
            return BarakoTracing.JobChanged;
        }

        var outcome = deadLettered ? JobMetrics.DeadLettered : JobMetrics.Retried;
        Metrics.Recorded(outcome);
        return outcome;
    }

    /// <summary>
    /// Hourly. Completed records are deleted. A job that expired without ever completing is
    /// dead-lettered rather than deleted, because an operator has to be able to see it.
    /// </summary>
    public async Task PurgeStaleJobsAsync(StaleJobSearchParams<JobRecord> p)
    {
        var ct = p.CancellationToken;
        await _gate.WaitAsync(ct);

        IReadOnlyList<JobRecord> stale;
        if (TenantPartitions.Enforced(_configuration))
        {
            stale = (await PerTenantAsync((q, remaining) => q
                    .Where(p.Match)
                    .Where(r => r.State != JobState.DeadLettered)
                    .Take(remaining), stopAt: PurgeBatchSize, ct))
                .Take(PurgeBatchSize)
                .ToList();
        }
        else
        {
            await using var query = _store.QuerySession();
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
                    record.CompletedAt = DateTime.UtcNow;
                    session.Store(record);
                }
            }

            await session.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// A query over every tenant's jobs when Postgres enforces the tenant filter.
    /// </summary>
    /// <remarks>
    /// <c>AnyTenant()</c> lifts Marten's own filter and nothing else. With database tenancy on, the
    /// row level security policy still holds a session to the tenant it was opened for, so a default
    /// session sees only the default partition's jobs and a worker never runs anybody else's (#877).
    /// So one session per partition from <see cref="TenantPartitions"/>, each setting its own tenant,
    /// and the caller orders and limits the merged result again. With <paramref name="stopAt"/> set,
    /// the shape is handed what is left of that budget and the walk ends once it is spent, so a
    /// cancel or a purge does not query every tenant after it already has what it needs.
    /// </remarks>
    private async Task<List<JobRecord>> PerTenantAsync(
        Func<IQueryable<JobRecord>, int, IQueryable<JobRecord>> shape, int? stopAt, CancellationToken ct)
    {
        var found = new List<JobRecord>();

        foreach (var tenantId in await TenantPartitions.FromRegistryAsync(_store, ct))
        {
            var remaining = stopAt is { } budget ? budget - found.Count : int.MaxValue;
            if (remaining <= 0) break;

            await using var session = _store.QuerySession(tenantId);
            found.AddRange(await shape(session.Query<JobRecord>(), remaining).ToListAsync(ct));
        }

        return found;
    }

    private static bool IsConcurrency(Exception ex) =>
        ex is JasperFx.ConcurrencyException || ex.GetType().Name.Contains("Concurrency");

    /// <summary>
    /// Type and message only. A stack trace is noise here and a response body can hold a credential.
    /// A URL in the message is cut to its scheme and host, since some webhook URLs carry a token.
    /// </summary>
    /// <remarks>Cut to length first, so a URL the cut runs through is still redacted.</remarks>
    internal static string Describe(Exception ex) =>
        UrlRedaction.InText(LogSafe.Text($"{ex.GetType().Name}: {ex.Message}", maxLength: 1000));

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
