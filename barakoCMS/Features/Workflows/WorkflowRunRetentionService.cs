using Marten;
using barakoCMS.Infrastructure.Retention;
using barakoCMS.Models;

namespace barakoCMS.Features.Workflows;

/// <summary>
/// Removes workflow runs that are old enough to be of no use, keeping failures longer than
/// successes.
/// </summary>
/// <remarks>
/// Every content publish leaves a run behind, at the rate content is published times the actions per
/// workflow, and until this existed nothing removed them.
///
/// One window would have been the wrong shape. A run that succeeded is interesting for about as long
/// as somebody might ask "did that go out", and a run that failed is interesting until somebody deals
/// with it. Two windows, both configurable, with the failure one an order of magnitude longer.
///
/// A run that has not finished is never removed, whatever its age. That is a rule rather than a
/// consequence of the windows, because a run whose provider has been unreachable for a fortnight is
/// still work somebody is waiting on, and the window would otherwise delete the email rather than
/// the record of it.
///
/// This is not an audit trail and must not be sold as one. The audit entries a retry writes are
/// separate documents and this does not touch them. A deployment that needs run history kept for
/// compliance needs an export, not a longer window, and <c>docs/workflow-runs.md</c> says so.
/// </remarks>
internal sealed class WorkflowRunRetentionService : BackgroundService
{
    /// <summary>
    /// A Postgres advisory lock key, arbitrary but fixed, and deliberately not the scheduler's.
    /// </summary>
    /// <remarks>
    /// Sharing a key with <c>ScheduledContentService</c> would make the two sweeps exclude each
    /// other for no reason: they touch different tables and there is no ordering between them. What
    /// this key stops is two instances of THIS sweep deleting the same batch, which is wasted work
    /// and a pile of concurrency exceptions rather than a correctness problem.
    /// </remarks>
    private const long SweepLockKey = 8_242_026_002L;

    public const string EnabledKey = "Workflows:Retention:Enabled";
    public const string SucceededDaysKey = "Workflows:Retention:Succeeded";
    public const string FailedDaysKey = "Workflows:Retention:Failed";

    public const int DefaultSucceededDays = 7;
    public const int DefaultFailedDays = 90;

    /// <summary>Runs per sweep, and sweeps per tick, so one pass cannot hold a connection all night.</summary>
    public const int DefaultBatchSize = 500;
    public const int DefaultMaxBatchesPerSweep = 20;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IDocumentStore _store;
    private readonly IConfiguration _config;
    private readonly ILogger<WorkflowRunRetentionService> _logger;

    public WorkflowRunRetentionService(
        IDocumentStore store, IConfiguration config, ILogger<WorkflowRunRetentionService> logger)
    {
        _store = store;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue(EnabledKey, true))
        {
            _logger.LogInformation("Workflow run retention is off. Runs will accumulate.");
            return;
        }

        // Late enough that a deployment finishes booting before anything is deleted, which keeps the
        // first minutes of a rollout free of a sweep nobody is watching.
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TrySweepAllTenantsAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Logged and retried on the next tick. A retention sweep failing is not a reason to
                // stop the host, and the rows it did not delete are still there next hour.
                _logger.LogError(ex, "Error during the workflow run retention sweep");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Sweeps every partition holding runs, if no other instance is already doing it.</summary>
    /// <returns>False when another instance held the lock and this tick did nothing.</returns>
    public async Task<bool> TrySweepAllTenantsAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var result = await RetentionSweeper.TrySweepAllTenantsAsync(
            _store, _config, SweepLockKey, PartitionsWithRunsSql, Declarations(Windows()),
            nowUtc, _logger, "Workflow run", ct);

        return result.LockHeld;
    }

    /// <summary>How long each class of finished run is kept.</summary>
    /// <remarks>
    /// Zero or less means keep forever, and that is the reading this had to choose between. "0 days"
    /// also reads as "delete immediately", and a setting whose two plain readings are opposite is not
    /// one to leave to a default. Keeping is the direction a mistake is recoverable from.
    /// </remarks>
    public RetentionWindows Windows() => ReadWindows(_config);

    /// <summary>The windows in force, for a caller that has the configuration and not the service.</summary>
    internal static RetentionWindows ReadWindows(IConfiguration config) => new(
        config.GetValue(SucceededDaysKey, DefaultSucceededDays),
        config.GetValue(FailedDaysKey, DefaultFailedDays));

    /// <summary>Whether the sweep runs at all. Off, nothing is removed whatever the windows say.</summary>
    internal static bool IsEnabled(IConfiguration config) => config.GetValue(EnabledKey, true);

    /// <summary>Partitions that hold at least one finished run, when the rows can be asked directly.</summary>
    /// <remarks>
    /// Terminal statuses only, cast to integer because Marten stores an enum as a number: the
    /// JsonStringEnumConverter in ServiceCollectionExtensions is the HTTP serializer.
    /// 2 Succeeded, 3 Failed, 4 PartiallyFailed, 5 Cancelled. Pending and Running are deliberately
    /// absent.
    /// </remarks>
    private const string PartitionsWithRunsSql =
        "select distinct tenant_id from public.mt_doc_workflow_runs "
      + "where (data ->> 'Status')::integer in (2, 3, 4, 5)";

    /// <summary>
    /// Deletes the finished runs past their window in one partition. Pure over the session, so a test
    /// drives it without the timer.
    /// </summary>
    public static Task<int> SweepTenantAsync(
        IDocumentSession session,
        DateTimeOffset nowUtc,
        RetentionWindows windows,
        ILogger? logger,
        CancellationToken ct) =>
        RetentionSweeper.SweepSessionAsync(session, session.TenantId, Declarations(windows), nowUtc, ct);

    private static readonly RunStatus[] FinishedStatuses =
        [RunStatus.Succeeded, RunStatus.Failed, RunStatus.PartiallyFailed, RunStatus.Cancelled];

    /// <summary>
    /// Succeeded on its own, then the three that contain a failure. PartiallyFailed is grouped with
    /// Failed on purpose: it holds at least one action nobody has dealt with, and the reason this
    /// has two windows is that such a run stays interesting. Cancelled goes with them: it holds
    /// an action that never went out, and somebody asking why wants it for as long as a failure.
    /// </summary>
    internal static IReadOnlyList<RetentionDeclaration> Declarations(RetentionWindows windows) =>
    [
        ForStatuses("Succeeded workflow runs", windows.SucceededDays, [RunStatus.Succeeded]),
        ForStatuses("Failed workflow runs", windows.FailedDays,
            [RunStatus.Failed, RunStatus.PartiallyFailed, RunStatus.Cancelled]),
    ];

    private static RetentionDeclaration ForStatuses(string name, int days, RunStatus[] statuses) =>
        new RetentionDeclaration<WorkflowRun>(
            name,
            // Aged on when it finished, falling back to when it was created. A finished run should
            // always carry a completion time; one that does not would otherwise be immortal, which is
            // the failure mode a retention sweep exists to prevent.
            cutoff => r => statuses.Contains(r.Status)
                        && ((r.CompletedAt != null && r.CompletedAt < cutoff)
                         || (r.CompletedAt == null && r.CreatedAt < cutoff)),
            _ => days <= 0 ? null : TimeSpan.FromDays(days),
            // A run that has not finished is never removed, so a partition holding only those is empty here.
            r => FinishedStatuses.Contains(r.Status));
}

/// <summary>How many days each class of finished run is kept. Zero or less keeps them forever.</summary>
/// <remarks>
/// Internal, like everything else under Features. CLAUDE.md section 6 puts the whole of Features
/// outside the package surface, and PublicSurfaceTests enforces it: this shipped public and that
/// gate is what said so, which is the gate working rather than a nuisance.
/// </remarks>
internal sealed record RetentionWindows(int SucceededDays, int FailedDays);
