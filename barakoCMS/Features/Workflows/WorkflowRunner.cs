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
/// Executes queued workflow actions, one at a time, outside the projection.
/// </summary>
/// <remarks>
/// The projection decides and records; this does the I/O. That is the whole point of #329: an
/// action that posts to three third parties used to hold a Marten daemon shard for the duration,
/// so a slow provider stalled workflow processing for every tenant and a hanging one stopped it.
/// </remarks>
internal sealed class WorkflowRunner : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _services;
    private readonly ILogger<WorkflowRunner> _logger;
    private readonly IConfiguration _config;
    private readonly string _node = $"{Environment.MachineName}-{Guid.NewGuid():N}"[..40];
    private readonly Random _random = new();

    public WorkflowRunner(IServiceProvider services, ILogger<WorkflowRunner> logger, IConfiguration config)
    {
        _services = services;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Workflows:RunnerEnabled", true))
        {
            _logger.LogInformation("Workflows:RunnerEnabled is off, so queued workflow actions will not run.");
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
                _logger.LogError(ex, "The workflow runner failed a pass and will try again");
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

    /// <summary>Claims one attempt and runs it. Returns whether there was anything to do.</summary>
    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        foreach (var tenantId in await PartitionsWithWorkAsync(store, ct))
        {
            await using var query = store.QuerySession(tenantId);

            var due = await query.Query<WorkflowRun>()
                .Where(r => r.Status == RunStatus.Pending || r.Status == RunStatus.Running)
                .OrderBy(r => r.CreatedAt)
                .Take(20)
                .ToListAsync(ct);

            foreach (var candidate in due)
            {
                if (await TryRunAsync(store, candidate.Id, tenantId, ct)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The partitions that actually hold unfinished work.
    /// </summary>
    /// <remarks>
    /// Read from the runs table rather than from the tenant registry, which is what
    /// ScheduledContentService does. The registry is the right source for a sweep that visits every
    /// tenant looking for something to do; it is the wrong one for draining a queue, because a run
    /// queued before a tenant was deactivated would then never execute and nothing would say so.
    /// Asking the rows themselves cannot miss one.
    ///
    /// Distinct tenant ids only, so this is one index-backed query rather than a scan per tenant.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> PartitionsWithWorkAsync(IDocumentStore store, CancellationToken ct)
    {
        var partitions = new List<string>();

        await using var conn = store.Storage.Database.CreateConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "select distinct tenant_id from public.mt_doc_workflow_runs "
          + "where (data ->> 'Status')::integer in (0, 1)";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            partitions.Add(reader.GetString(0));
        }

        return partitions;
    }

    /// <summary>Claims the next due attempt of one run and executes it.</summary>
    private async Task<bool> TryRunAsync(IDocumentStore store, Guid runId, string tenantId, CancellationToken ct)
    {
        WorkflowActionAttempt? claimed;
        WorkflowRun run;

        // The claim is its own transaction. Optimistic concurrency on the run is what stops two
        // nodes taking the same attempt: both load it, both write, and the second is refused. A lock
        // would serialise every node onto one attempt at a time, which is the shape the scheduler
        // needs and the wrong one here.
        await using (var session = store.LightweightSession(tenantId))
        {
            run = (await session.LoadAsync<WorkflowRun>(runId, ct))!;
            if (run is null) return false;

            claimed = NextDue(run);
            if (claimed is null) return false;

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
                return false;
            }
        }

        var outcome = await ExecuteAsync(store, run, claimed, tenantId, ct);

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
                _logger.LogWarning(
                    "Discarding the outcome of run {RunId} action {Ordinal}: the lease is now held by "
                  + "{Holder} and the attempt is {Status}. This node ran past its lease.",
                    runId, claimed.Ordinal, attempt.LeasedBy ?? "(nobody)", attempt.Status);

                return true;
            }

            Apply(attempt, outcome);
            latest.Recompute();
            session.Update(latest);

            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is JasperFx.ConcurrencyException
                || ex.GetType().Name.Contains("Concurrency"))
            {
                // The outcome is lost, the lease expires, and the attempt runs again. That is why
                // the idempotency key is stable across attempts rather than generated per try.
                _logger.LogWarning("Could not record the outcome of run {RunId} action {Ordinal}", runId, claimed.Ordinal);
            }
        }

        return true;
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
        using var scope = _services.CreateScopeForTenant(tenantId);
        var handlers = scope.ServiceProvider.GetServices<IWorkflowAction>();
        var handler = handlers.FirstOrDefault(h => h.Type == attempt.ActionType);

        if (handler is null)
        {
            // Permanent: no amount of waiting registers a handler that the host was not built with.
            return new Outcome(AttemptStatus.Failed, $"No handler is registered for action type '{attempt.ActionType}'.", 0, Retryable: false);
        }

        await using var session = store.LightweightSession(tenantId);
        var content = await session.LoadAsync<barakoCMS.Models.Content>(run.ContentId, ct);

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

            var resolved = new Dictionary<string, string>(attempt.Parameters.Count);
            foreach (var (key, value) in attempt.Parameters)
            {
                resolved[key] = variables.ResolveVariables(value, content);
            }

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
            // credential that was sent, and this string is stored, served over the API and shown in
            // the admin.
            _logger.LogWarning(ex, "Workflow action {Type} failed in run {RunId}", attempt.ActionType, run.Id);
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
            attempt.NextAttemptAt = DateTimeOffset.UtcNow.Add(WorkflowRetryPolicy.Backoff(attempt.Attempts, _random));
            return;
        }

        attempt.Status = outcome.Status;
        attempt.NextAttemptAt = null;
        attempt.CompletedAt = DateTimeOffset.UtcNow;
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "...";

    private readonly record struct Outcome(AttemptStatus Status, string? Error, long ElapsedMs, bool Retryable = true);
}
