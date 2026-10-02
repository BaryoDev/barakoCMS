using System.Diagnostics;
using JasperFx;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Marten.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace barakoCMS.Features.Workflows;

/// <summary>How hard the runner tries, and how it backs off.</summary>
/// <remarks>
/// Internal, like everything under Features. These are the runner's own numbers rather than an
/// extension point, and making them public would freeze them as contract under section 6 for
/// nobody's benefit. Tests reach them through InternalsVisibleTo.
/// </remarks>
internal static class WorkflowRetryPolicy
{
    /// <summary>
    /// Attempts before an action is left Failed.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. A run that retries forever is a self-inflicted denial of service against
    /// a third party, who answers by rate-limiting or banning the account, which takes down every
    /// other integration pointed at them.
    /// </remarks>
    public const int MaxAttempts = 5;

    /// <summary>How long a node may hold an attempt before another may take it.</summary>
    /// <remarks>
    /// Long enough that a slow provider does not lose its lease mid-call, short enough that a node
    /// which died does not strand work for long. Nothing has to notice the death: the lease simply
    /// stops being honoured.
    /// </remarks>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>Exponential with jitter, so a provider that failed for everyone is not retried by everyone at once.</summary>
    public static TimeSpan Backoff(int attempts, Random random)
    {
        var seconds = Math.Min(Math.Pow(2, Math.Max(attempts, 1)) * 5, 600);
        var jitter = random.NextDouble() * seconds * 0.25;
        return TimeSpan.FromSeconds(seconds + jitter);
    }
}

/// <summary>
/// Executes queued workflow actions outside the projection, a bounded number at a time.
/// </summary>
/// <remarks>
/// The projection decides and records; this does the I/O. That is the whole point of #329: an
/// action that posts to three third parties used to hold a Marten daemon shard for the duration,
/// so a slow provider stalled workflow processing for every tenant and a hanging one stopped it.
///
/// Actions of different runs may be in flight together, up to <see cref="ConcurrencyKey"/> on this
/// node. Actions of one run never are: the next one cannot be claimed until the one before it has
/// recorded its outcome or lost its lease.
/// </remarks>
internal sealed class WorkflowRunner(
    IServiceProvider services,
    ILogger<WorkflowRunner> logger,
    IConfiguration config) : BackgroundService
{
    internal static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    internal const int CandidatesPerPass = 20;

    public const string ConcurrencyKey = "Workflows:RunnerConcurrency";

    /// <summary>One action at a time per node, as it was before the setting existed.</summary>
    public const int DefaultConcurrency = 1;

    /// <summary>
    /// A pass reads <see cref="CandidatesPerPass"/> runs per tenant, so one tenant could not fill
    /// more slots than that.
    /// </summary>
    public const int MaxConcurrency = CandidatesPerPass;

    private readonly string _node = $"{Environment.MachineName}-{Guid.NewGuid():N}"[..40];
    private readonly int _concurrency = ReadConcurrency(config);

    private string[]? _partitions;
    private long _scannedAt;
    private string? _lastServed;

    /// <summary>How many times this runner has listed the partitions.</summary>
    internal int PartitionScans { get; private set; }

    /// <summary>How many actions of different runs this node may have in flight at once.</summary>
    /// <remarks>
    /// Refused out of range, not clamped. Every action is a call to a third party, so the number is
    /// how hard one node may hit a provider, and a typing mistake should stop the host from starting
    /// instead of quietly becoming some other number.
    /// </remarks>
    internal static int ReadConcurrency(IConfiguration config)
    {
        var value = config.GetValue(ConcurrencyKey, DefaultConcurrency);

        if (value is < 1 or > MaxConcurrency)
        {
            throw new InvalidOperationException(
                $"{ConcurrencyKey} must be between 1 and {MaxConcurrency}, and is {value}. "
              + "It is how many workflow actions one node runs at once. For more than that, add nodes.");
        }

        return value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Workflows:RunnerEnabled", true))
        {
            logger.LogInformation("Workflows:RunnerEnabled is off, so queued workflow actions will not run.");
            return;
        }

        // Nothing here may throw out of the loop. A BackgroundService that faults stops for the
        // lifetime of the process, and the symptom is workflows quietly never running again, which
        // is the state this whole feature exists to make impossible.
        while (!stoppingToken.IsCancellationRequested)
        {
            var did = false;

            try
            {
                did = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The workflow runner failed a pass and will try again");
            }

            if (!did)
            {
                try
                {
                    await Task.Delay(Idle, stoppingToken);
                }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Claims attempts up to the bound, runs them together and waits for all of them. Returns
    /// whether there was anything to do.
    /// </summary>
    /// <remarks>
    /// The partition list is kept between passes for up to <see cref="Idle"/>, so draining a backlog
    /// does not scan for partitions once per action. A pass that finds nothing in a kept list scans
    /// again before it reports idle, so false always means a fresh scan found nothing due.
    /// </remarks>
    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        var fresh = _partitions is null || Stopwatch.GetElapsedTime(_scannedAt) >= Idle;
        if (fresh) await ScanPartitionsAsync(store, ct);

        if (await RunDueAsync(store, ct)) return true;
        if (fresh) return false;

        await ScanPartitionsAsync(store, ct);
        return await RunDueAsync(store, ct);
    }

    private async Task ScanPartitionsAsync(IDocumentStore store, CancellationToken ct)
    {
        var listed = await TenantPartitions.ListAsync(store, config, PartitionsWithWorkSql, ct);

        // Sorted, because Postgres promises no order and the rotation below needs a stable one.
        _partitions = listed.OrderBy(slug => slug, StringComparer.Ordinal).ToArray();
        _scannedAt = Stopwatch.GetTimestamp();
        PartitionScans++;
    }

    /// <summary>What one pass has taken so far.</summary>
    /// <remarks>
    /// Made by the pass and handed down, never kept on the runner, so two passes on one runner
    /// cannot share one. Only the claiming loop of its pass reads or writes it. The actions it
    /// starts are handed what they need and never touch it.
    /// </remarks>
    private sealed class Pass(int slots)
    {
        public int Free = slots;

        public readonly List<Task> Running = [];

        /// <summary>The due runs of each tenant asked so far, less the ones already offered.</summary>
        public readonly Dictionary<string, Queue<Guid>> Due = new(StringComparer.Ordinal);
    }

    /// <summary>A run that took a slot. With no attempt, it was cancelled in place of being claimed.</summary>
    private sealed record Claim(WorkflowRun Run, WorkflowActionAttempt? Attempt);

    /// <summary>
    /// Fills the slots of one pass, a round of the tenants at a time, then waits for what it started.
    /// </summary>
    /// <remarks>
    /// A round gives each tenant at most one slot, so a tenant with a long queue gets a second one
    /// only after the round has been past every other tenant with work. The rounds stop when the
    /// slots are full or a round took none.
    ///
    /// Everything started is awaited here, also when claiming throws, so no action outlives the
    /// pass that started it and the bound holds from one pass to the next.
    /// </remarks>
    private async Task<bool> RunDueAsync(IDocumentStore store, CancellationToken ct)
    {
        var pass = new Pass(_concurrency);

        try
        {
            int before;

            do
            {
                before = pass.Free;
                await ClaimRoundAsync(store, pass, ct);
            }
            while (pass.Free > 0 && pass.Free < before);
        }
        finally
        {
            await Task.WhenAll(pass.Running);
        }

        return pass.Free < _concurrency;
    }

    /// <summary>
    /// One round over the kept partitions, starting after the one served last.
    /// </summary>
    /// <remarks>
    /// Always starting from the top let a tenant that sorts early and always has work keep every
    /// tenant after it waiting.
    ///
    /// A tenant that throws is logged and passed over for the rest of the pass. The next pass
    /// starts after it, the same as after a tenant that was served, or one run that cannot be read
    /// would be where every pass starts and ends.
    /// </remarks>
    private async Task ClaimRoundAsync(IDocumentStore store, Pass pass, CancellationToken ct)
    {
        var partitions = _partitions!;
        var start = 0;

        if (_lastServed is not null)
        {
            start = Array.FindIndex(partitions, slug => string.CompareOrdinal(slug, _lastServed) > 0);
            if (start < 0) start = 0;
        }

        for (var i = 0; i < partitions.Length && pass.Free > 0; i++)
        {
            var tenantId = partitions[(start + i) % partitions.Length];

            try
            {
                if (await RunDueInAsync(store, tenantId, pass, ct))
                {
                    _lastServed = tenantId;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _lastServed = tenantId;
                pass.Due[tenantId] = new();
                logger.LogError(ex,
                    "The workflow runner failed in tenant {Tenant} and went on to the next",
                    barakoCMS.Infrastructure.Logging.LogSafe.Value(tenantId));
            }
        }
    }

    /// <summary>Gives one tenant the next free slot, if it has a run that takes it.</summary>
    /// <remarks>
    /// A tenant's due runs are read once per pass and kept, so a later round does not query again
    /// and a run is offered at most once per pass.
    ///
    /// The action starts on the pool as soon as it is claimed, so its lease is not spent waiting
    /// for the other slots to fill, and a handler that works before its first await does not hold
    /// up the claiming.
    /// </remarks>
    private async Task<bool> RunDueInAsync(IDocumentStore store, string tenantId, Pass pass, CancellationToken ct)
    {
        if (!pass.Due.TryGetValue(tenantId, out var due))
        {
            due = new Queue<Guid>(await DueRunsAsync(store, tenantId, ct));
            pass.Due[tenantId] = due;
        }

        while (due.TryDequeue(out var runId))
        {
            var claim = await TryClaimAsync(store, runId, tenantId, ct);
            if (claim is null) continue;

            pass.Free--;

            if (claim.Attempt is { } attempt)
            {
                var run = claim.Run;
                pass.Running.Add(Task.Run(
                    () => RunClaimedAsync(store, run, attempt, tenantId, ct), CancellationToken.None));
            }

            return true;
        }

        return false;
    }

    /// <summary>The oldest runs of one tenant that can be claimed now.</summary>
    private static async Task<IReadOnlyList<Guid>> DueRunsAsync(IDocumentStore store, string tenantId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        await using var query = store.QuerySession(tenantId);

        // Due-ness is in the query so the twenty are twenty runs that can be claimed. A run stored
        // before NextDueAt was kept has none and is read as due; TryClaimAsync decides.
        //
        // Not indexed. Marten writes the null test on the raw JSON value and the comparison on
        // mt_immutable_timestamptz of it, and Postgres uses an index for an OR only when every arm
        // matches one. The null arm has to stay while an older node can still write a run without
        // the value, so an index on the comparison alone would be maintained and never read.
        return await query.Query<WorkflowRun>()
            .Where(r => (r.Status == RunStatus.Pending || r.Status == RunStatus.Running)
                && (r.NextDueAt == null || r.NextDueAt <= now))
            .OrderBy(r => r.CreatedAt)
            .Take(CandidatesPerPass)
            .Select(r => r.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The partitions that actually hold unfinished work, when the rows can be asked directly.
    /// </summary>
    /// <remarks>
    /// Rows rather than the registry, which is what ScheduledContentService reads. The registry is
    /// the right source for a sweep that visits every tenant looking for something to do; it is the
    /// wrong one for draining a queue, because a partition it does not list would never execute and
    /// nothing would say so. Asking the rows themselves cannot miss one.
    ///
    /// Only with database tenancy off. With it on, <see cref="TenantPartitions"/> reads the registry
    /// instead, including inactive tenants, and the due query in <see cref="RunOnceAsync"/> is what
    /// skips a partition with nothing to do.
    ///
    /// The cast in the filter is the expression the Status index is declared on, so the index can
    /// serve it.
    /// </remarks>
    internal const string PartitionsWithWorkSql =
        "select distinct tenant_id from public.mt_doc_workflow_runs where " + UnfinishedFilter;

    internal const string UnfinishedFilter = "(data ->> 'Status')::integer in (0, 1)";

    /// <summary>Claims the next due attempt of one run, or cancels what a stopped run has left.</summary>
    /// <returns>Null when nothing was written, which leaves the slot for the next run.</returns>
    private async Task<Claim?> TryClaimAsync(IDocumentStore store, Guid runId, string tenantId, CancellationToken ct)
    {
        // The claim is its own transaction. Optimistic concurrency on the run is what stops two
        // nodes taking the same attempt: both load it, both write, and the second is refused. A lock
        // would serialise every node onto one attempt at a time, which is the shape the scheduler
        // needs and the wrong one here.
        await using (var session = store.LightweightSession(tenantId))
        {
            var run = await session.LoadAsync<WorkflowRun>(runId, ct);
            if (run is null) return null;

            if (run.CancelledAt is not null || await IsSwitchedOffAsync(session, run.WorkflowDefinitionId, ct))
            {
                return await CancelRemainingAsync(session, run, ct) ? new Claim(run, null) : null;
            }

            var claimed = NextDue(run);
            if (claimed is null)
            {
                await RecordNextDueAsync(session, run, ct);
                return null;
            }

            claimed.Status = AttemptStatus.Running;
            claimed.LeasedBy = _node;
            claimed.LeaseExpiresAt = DateTimeOffset.UtcNow.Add(WorkflowRetryPolicy.LeaseDuration);
            run.Recompute();
            session.Update(run);

            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is JasperFx.ConcurrencyException
                || ex.GetType().Name.Contains("Concurrency"))
            {
                // Another node got there first. Not an error, and not worth a log line at warning:
                // it is the mechanism working.
                return null;
            }

            return new Claim(run, claimed);
        }
    }

    /// <summary>Executes one claimed attempt and records how it went. Never throws.</summary>
    /// <remarks>
    /// It runs beside the other attempts of its pass, so whatever goes wrong here is logged and ends
    /// here. Thrown on, it would be the pass that failed, with the other outcomes unread. The
    /// attempt is left Running under its lease, and is taken again when the lease ends.
    /// </remarks>
    private async Task RunClaimedAsync(
        IDocumentStore store, WorkflowRun run, WorkflowActionAttempt claimed, string tenantId, CancellationToken ct)
    {
        try
        {
            await ExecuteAndRecordAsync(store, run, claimed, tenantId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host is stopping.
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "The workflow runner failed on run {RunId} action {Ordinal} in tenant {Tenant}",
                run.Id, claimed.Ordinal, barakoCMS.Infrastructure.Logging.LogSafe.Value(tenantId));
        }
    }

    private async Task ExecuteAndRecordAsync(
        IDocumentStore store, WorkflowRun run, WorkflowActionAttempt claimed, string tenantId, CancellationToken ct)
    {
        var runId = run.Id;
        var outcome = await ExecuteAsync(store, run, claimed, tenantId, ct);

        // Twice, because a cancel or a delete may write the run between the read and the save here.
        // Either leaves this attempt Running under this node's lease, so a second read can still
        // record what happened. Left unrecorded on a stopped run, the attempt is never run again,
        // and an action that completed would read as one nobody knows the outcome of.
        if (!await RecordAsync(store, runId, tenantId, claimed, outcome, ct)
            && !await RecordAsync(store, runId, tenantId, claimed, outcome, ct))
        {
            // The outcome is lost. On a run nobody stopped the lease expires and the attempt runs
            // again, which is why the idempotency key is stable across attempts rather than
            // generated per try. On a stopped run it is marked Unknown when the lease ends.
            logger.LogWarning("Could not record the outcome of run {RunId} action {Ordinal}", runId, claimed.Ordinal);
        }
    }

    /// <summary>Writes one attempt's outcome onto the run as it is stored now.</summary>
    /// <returns>
    /// False when the save was refused because the run was written in between. True otherwise,
    /// which includes there being nothing to write: the run or the attempt gone, or the lease lost.
    /// </returns>
    private async Task<bool> RecordAsync(
        IDocumentStore store, Guid runId, string tenantId, WorkflowActionAttempt claimed, Outcome outcome, CancellationToken ct)
    {
        await using (var session = store.LightweightSession(tenantId))
        {
            var latest = await session.LoadAsync<WorkflowRun>(runId, ct);
            if (latest is null) return true;

            var attempt = latest.Actions.FirstOrDefault(a => a.Ordinal == claimed.Ordinal);
            if (attempt is null) return true;

            // The lease has to still be ours. A handler that outlives its lease lets another node
            // reclaim the attempt and start it again, and this block would then write the first
            // node's outcome over the second node's work and clear a lease it does not hold. The
            // second node keeps running, finishes, and finds the attempt already terminal, so the
            // visible result is one action performed twice and one outcome recorded.
            //
            // Dropping the outcome is the right answer rather than a loss: the node that holds the
            // lease is the one whose result is current, and the idempotency key is stable across
            // attempts precisely so the duplicate call is absorbed downstream.
            if (attempt.Status != AttemptStatus.Running || attempt.LeasedBy != _node)
            {
                logger.LogWarning(
                    "Discarding the outcome of run {RunId} action {Ordinal}: the lease is now held by "
                  + "{Holder} and the attempt is {Status}. This node ran past its lease.",
                    runId, claimed.Ordinal, attempt.LeasedBy ?? "(nobody)", attempt.Status);

                return true;
            }

            Apply(attempt, outcome);

            // Stopped while this attempt was out. It ran, so its outcome is recorded as it
            // happened, and a failure that would have been queued again stays a failure.
            if (latest.CancelledAt is not null && attempt.Status == AttemptStatus.Pending)
            {
                attempt.Status = AttemptStatus.Failed;
                attempt.Retryable = true;
                attempt.NextAttemptAt = null;
                attempt.CompletedAt = DateTimeOffset.UtcNow;
            }

            latest.Recompute();
            session.Update(latest);

            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is JasperFx.ConcurrencyException
                || ex.GetType().Name.Contains("Concurrency"))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the workflow a run belongs to has been switched off since the run was queued.</summary>
    /// <remarks>
    /// Asked at every claim, so a run stops between two of its actions too. A run whose definition
    /// is gone is not stopped here: deleting through the API cancels the queued runs itself, and a
    /// run stored with no definition behind it has always run.
    /// </remarks>
    internal static async Task<bool> IsSwitchedOffAsync(IQuerySession session, Guid workflowId, CancellationToken ct) =>
        await session.LoadAsync<WorkflowDefinition>(workflowId, ct) is { Enabled: false };

    /// <summary>
    /// Cancels what is left of a run that was stopped, or whose workflow is switched off, in place
    /// of claiming it.
    /// </summary>
    /// <remarks>
    /// The same optimistic write the claim makes. Refused, another node or the cancel endpoint wrote
    /// the run first, and the run is offered again on a later pass if anything of it is still due.
    /// </remarks>
    /// <returns>
    /// Whether the run was written. That counts as work done, so the pass ends and the next starts
    /// at once: a switched off workflow with a long queue is cleared without an idle wait every
    /// twenty runs, and without holding up the due runs behind it. Nothing written is not work, or a
    /// run with nothing left to cancel would be a pass that never ends.
    /// </returns>
    internal static async Task<bool> CancelRemainingAsync(IDocumentSession session, WorkflowRun run, CancellationToken ct)
    {
        var stopped = run.CancelledAt;
        var stored = run.NextDueAt;

        var moved = run.Cancel(DateTimeOffset.UtcNow);
        if (moved == 0 && run.CancelledAt == stopped && run.NextDueAt == stored) return false;

        session.Update(run);

        try
        {
            await session.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException
            || ex.GetType().Name.Contains("Concurrency"))
        {
            // Another writer got there first, and its write stands.
            return false;
        }
    }

    /// <summary>
    /// Writes NextDueAt on a run the query offered that turned out not to be due.
    /// </summary>
    /// <remarks>
    /// Only a run stored without the value gets here in practice. Left alone it would be offered on
    /// every pass until its wait ran out, taking one of the slots a due run needs.
    /// </remarks>
    private static async Task RecordNextDueAsync(IDocumentSession session, WorkflowRun run, CancellationToken ct)
    {
        var stored = run.NextDueAt;
        run.Recompute();
        if (run.NextDueAt == stored) return;

        session.Update(run);

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException
            || ex.GetType().Name.Contains("Concurrency"))
        {
            // Another node wrote the run first, and its write carries the value.
        }
    }

    /// <summary>
    /// The attempt that should run next, or null.
    /// </summary>
    /// <remarks>
    /// Strictly in order, and only when everything before it has finished. "Post to Facebook, then
    /// email, then tweet" reads as a sequence and an operator who wrote it that way means it.
    ///
    /// A failed action does not stop the ones after it: they run and the run is marked
    /// PartiallyFailed. These are usually independent, and skipping the tweet because the mail
    /// server was down is a surprise nobody asked for. That choice is #329's recommendation and is
    /// recorded here rather than left to fall out of the loop.
    /// </remarks>
    private static WorkflowActionAttempt? NextDue(WorkflowRun run)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var attempt in run.Actions.OrderBy(a => a.Ordinal))
        {
            // Running and still leased belongs to somebody else. Running with an expired lease is a
            // node that died, and is fair game.
            if (attempt.Status == AttemptStatus.Running)
            {
                if (attempt.LeaseExpiresAt > now) return null;
                return attempt;
            }

            if (attempt.Status != AttemptStatus.Pending) continue;

            if (attempt.NextAttemptAt is { } due && due > now) continue;

            return attempt;
        }

        return null;
    }

    private async Task<Outcome> ExecuteAsync(
        IDocumentStore store, WorkflowRun run, WorkflowActionAttempt attempt, string tenantId, CancellationToken ct)
    {
        using var scope = services.CreateScopeForTenant(tenantId);
        var handlers = scope.ServiceProvider.GetServices<IWorkflowAction>();
        var handler = handlers.FirstOrDefault(h => h.Type == attempt.ActionType);

        if (handler is null)
        {
            // Permanent: no amount of waiting registers a handler that the host was not built with.
            return new Outcome(AttemptStatus.Failed, $"No handler is registered for action type '{attempt.ActionType}'.", 0, Retryable: false);
        }

        // Resolved from the scope, not a separately opened store.LightweightSession(tenantId), and
        // not disposed here: an IWorkflowAction is constructed from this same scope (the handler
        // lookup above already built one, for every registered action type) and receives this exact
        // instance through DI, since Marten registers IDocumentSession scoped and a scope caches the
        // first resolution. Content now carries real optimistic concurrency (#565 / D16), and a
        // handler that stores the triggering content back (UpdateFieldAction, when no TargetId is
        // given) does it through that DI session; loading content through a second, separate session
        // here meant the handler's Store() had no version Marten could vouch for and was refused
        // outright, which read from the outside as "the workflow never fired". One session for the
        // whole attempt is what keeps the load-then-store pattern every other write in this codebase
        // relies on.
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        // An erased entry cannot be loaded, and a Deleted action is told only which entry went.
        var content = run.TriggerEvent == WorkflowEvents.Deleted
            ? new ErasedContent(run.ContentId, run.ContentType)
            : await session.LoadAsync<barakoCMS.Models.Content>(run.ContentId, ct);

        if (content is null)
        {
            // Skipped rather than failed. The entry was deleted after the run was queued, so there
            // is nothing to send and nothing anybody can do about it: reporting it as a failure puts
            // a permanent red mark on a screen with no action behind it.
            return new Outcome(AttemptStatus.Skipped, "The content no longer exists.", 0);
        }

        var timer = Stopwatch.StartNew();

        try
        {
            var variables = scope.ServiceProvider.GetRequiredService<ITemplateVariableExtractor>();

            var (parameters, credentialError) = barakoCMS.Features.Workflows.Actions.WebhookSigning.UnprotectCredentials(
                attempt.Parameters, scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Security.ISecretProtector>());

            if (credentialError is not null)
            {
                // Permanent: the key that would decrypt it is not coming back on a retry.
                timer.Stop();
                return new Outcome(AttemptStatus.Failed, credentialError, timer.ElapsedMilliseconds, Retryable: false);
            }

            var resolved = ActionParameters.Resolve(variables, attempt.ActionType, parameters, content);

            resolved["IdempotencyKey"] = attempt.IdempotencyKey;

            // What the delivery log needs to say which run a delivery belonged to. Same channel as
            // the idempotency key, because the parameters are the only thing an action receives.
            resolved["RunId"] = run.Id.ToString();
            resolved["WorkflowId"] = run.WorkflowDefinitionId.ToString();
            resolved["TriggerEvent"] = run.TriggerEvent;
            resolved["Attempt"] = (attempt.Attempts + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

            var result = await handler.RunAsync(resolved, content, ct);
            timer.Stop();

            if (result.Succeeded)
            {
                return new Outcome(AttemptStatus.Succeeded, null, timer.ElapsedMilliseconds);
            }

            return new Outcome(AttemptStatus.Failed, result.Error, timer.ElapsedMilliseconds, result.Retryable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timer.Stop();

            // A timeout is not a failure, it is an unknown: the request may have arrived and the
            // response been lost. Retrying it automatically is how a customer gets two invoices.
            return new Outcome(AttemptStatus.Unknown, "The request timed out, so it is not known whether it arrived.", timer.ElapsedMilliseconds);
        }
        catch (TaskCanceledException)
        {
            timer.Stop();
            return new Outcome(AttemptStatus.Unknown, "The request timed out, so it is not known whether it arrived.", timer.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            timer.Stop();

            // Keep the exception message in logs only. A provider's error body can carry the
            // credential that was sent, so only the exception type reaches the run record; the
            // message stays in the log above.
            logger.LogWarning(ex, "Workflow action {Type} failed in run {RunId}", attempt.ActionType, run.Id);
            return new Outcome(AttemptStatus.Failed, ex.GetType().Name, timer.ElapsedMilliseconds);
        }
    }

    private void Apply(WorkflowActionAttempt attempt, Outcome outcome)
    {
        attempt.Attempts++;
        attempt.LeasedBy = null;
        attempt.LeaseExpiresAt = null;
        attempt.DurationMs = outcome.ElapsedMs;
        attempt.Error = outcome.Error is null ? null : Truncate(outcome.Error);

        if (outcome.Status == AttemptStatus.Failed
            && outcome.Retryable
            && attempt.Attempts < WorkflowRetryPolicy.MaxAttempts)
        {
            attempt.Status = AttemptStatus.Pending;
            attempt.Retryable = null;
            attempt.NextAttemptAt = DateTimeOffset.UtcNow.Add(WorkflowRetryPolicy.Backoff(attempt.Attempts, Random.Shared));
            return;
        }

        attempt.Status = outcome.Status;
        attempt.Retryable = outcome.Status == AttemptStatus.Failed ? outcome.Retryable : null;
        attempt.NextAttemptAt = null;
        attempt.CompletedAt = DateTimeOffset.UtcNow;
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "...";

    private readonly record struct Outcome(AttemptStatus Status, string? Error, long ElapsedMs, bool Retryable = true);
}
