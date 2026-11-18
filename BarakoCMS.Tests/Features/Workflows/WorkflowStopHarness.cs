using System.Net.Http.Headers;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// What the disable, delete and cancel tests share: seeding a workflow and its runs in the default
/// tenant, and driving the runner with the fixture's hosted one stopped.
/// </summary>
/// <remarks>
/// A seeded attempt is parked an hour out unless a test asks for it due, because the hosted runner
/// polls the same database and claims anything due. A test that needs a due run seeds it, or makes
/// it due, inside <see cref="WithHostedRunnerPausedAsync"/>, so the only runner is the one the test
/// drives and what a run looks like after a drain is not a race.
/// </remarks>
internal sealed class WorkflowStopHarness(IntegrationTestFixture factory)
{
    public const string Counting = "CountingRunner";

    public const string OtherNode = "a-node-that-is-not-this-one";

    /// <summary>Polls of a drain before it checks that the backlog is shrinking.</summary>
    private const int PollsPerCheck = 200;

    // The runs this harness seeded in the default tenant, which is what a drain waits on.
    private readonly List<Guid> _seeded = [];

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static DateTimeOffset ParkedUntil => DateTimeOffset.UtcNow.AddHours(1);

    public IDocumentStore Store => factory.Services.GetRequiredService<IDocumentStore>();

    public static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    public HttpClient Anonymous() => factory.CreateClient();

    public Task<HttpClient> AdminAsync() => ClientAsync("SuperAdmin", "Admin");

    /// <summary>A signed-in caller whose role holds no capability.</summary>
    public Task<HttpClient> WithoutTheCapabilityAsync() => ClientAsync("User");

    private async Task<HttpClient> ClientAsync(params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await factory.StoredUserTokenAsync(roles));
        return client;
    }

    public async Task<Guid> StoreWorkflowAsync(Action<WorkflowDefinition>? shape = null)
    {
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = NewName("1009-wf"),
            TriggerContentType = NewName("post"),
            TriggerEvent = WorkflowEvents.Published,
            Actions = [new WorkflowAction { Type = Counting }],
        };
        shape?.Invoke(workflow);

        await using var session = Store.LightweightSession();
        session.Store(workflow);
        await session.SaveChangesAsync(Ct);

        return workflow.Id;
    }

    public async Task<WorkflowDefinition?> LoadWorkflowAsync(Guid id)
    {
        await using var session = Store.QuerySession();
        return await session.LoadAsync<WorkflowDefinition>(id, Ct);
    }

    /// <summary>An entry for the runner to load. Without one it skips the action and calls nothing.</summary>
    public async Task<Guid> StoreContentAsync(Dictionary<string, object>? data = null)
    {
        var id = Guid.NewGuid();

        await using var session = Store.LightweightSession();
        session.Store(new Content
        {
            Id = id,
            ContentType = "article",
            Status = ContentStatus.Published,
            Data = data ?? new Dictionary<string, object>(),
        });
        await session.SaveChangesAsync(Ct);

        return id;
    }

    public static WorkflowActionAttempt Waiting(string type = Counting, bool due = false) => new()
    {
        ActionType = type,
        NextAttemptAt = due ? null : ParkedUntil,
    };

    public static WorkflowActionAttempt RunningElsewhere() => new()
    {
        ActionType = Counting,
        Status = AttemptStatus.Running,
        LeasedBy = OtherNode,
        LeaseExpiresAt = ParkedUntil,
    };

    public static WorkflowActionAttempt Finished(AttemptStatus status) => new()
    {
        ActionType = Counting,
        Status = status,
        Attempts = 1,
        CompletedAt = DateTimeOffset.UtcNow,
    };

    public async Task<WorkflowRun> SeedRunAsync(
        Guid workflowId, Guid contentId, IReadOnlyList<WorkflowActionAttempt> attempts, DateTimeOffset? cancelledAt = null,
        DateTimeOffset? createdAt = null)
    {
        var run = NewRun(workflowId, contentId, attempts, cancelledAt);
        if (createdAt is { } at) run.CreatedAt = at;

        await using var session = Store.LightweightSession();
        session.Store(run);
        await session.SaveChangesAsync(Ct);

        _seeded.Add(run.Id);
        return run;
    }

    public async Task<List<WorkflowRun>> SeedRunsAsync(Guid workflowId, Guid contentId, int count)
    {
        var runs = Enumerable.Range(0, count)
            .Select(_ => NewRun(workflowId, contentId, [Waiting()], cancelledAt: null))
            .ToList();

        await using var session = Store.LightweightSession();
        foreach (var run in runs) session.Store(run);
        await session.SaveChangesAsync(Ct);

        _seeded.AddRange(runs.Select(r => r.Id));
        return runs;
    }

    private static WorkflowRun NewRun(
        Guid workflowId, Guid contentId, IReadOnlyList<WorkflowActionAttempt> attempts, DateTimeOffset? cancelledAt)
    {
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            WorkflowName = "Stop me",
            ContentId = contentId,
            ContentType = "article",
            TriggerEvent = WorkflowEvents.Published,
            TriggeringEventSequence = 1,
            CancelledAt = cancelledAt,
        };

        for (var i = 0; i < attempts.Count; i++)
        {
            attempts[i].Ordinal = i;
            attempts[i].IdempotencyKey = $"{run.Id:N}-{i}";
            run.Actions.Add(attempts[i]);
        }

        run.Recompute();
        return run;
    }

    public async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        await using var session = Store.QuerySession();
        var run = await session.LoadAsync<WorkflowRun>(runId, Ct);

        run.Should().NotBeNull();
        return run!;
    }

    public async Task<IReadOnlyList<WorkflowRun>> RunsOfAsync(Guid workflowId)
    {
        await using var session = Store.QuerySession();
        return await session.Query<WorkflowRun>()
            .Where(r => r.WorkflowDefinitionId == workflowId)
            .ToListAsync(Ct);
    }

    /// <summary>Ends the wait of every waiting attempt of a run. Call it with the hosted runner paused.</summary>
    public async Task MakeDueAsync(Guid runId)
    {
        await using var session = Store.LightweightSession();
        var run = await session.LoadAsync<WorkflowRun>(runId, Ct);
        run.Should().NotBeNull();

        foreach (var attempt in run!.Actions.Where(a => a.Status == AttemptStatus.Pending))
        {
            attempt.NextAttemptAt = null;
        }

        run.Recompute();
        session.Update(run);
        await session.SaveChangesAsync(Ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> AuditOfAsync(string action, Guid targetId)
    {
        var target = targetId.ToString();

        await using var session = Store.QuerySession();
        return await session.Query<AuditEvent>()
            .Where(e => e.Action == action && e.TargetId == target)
            .ToListAsync(Ct);
    }

    public static int TimesRun(WorkflowActionAttempt attempt) =>
        CountingRunnerAction.RunsByKey.GetValueOrDefault(attempt.IdempotencyKey);

    /// <summary>
    /// Runs the body with the fixture's hosted runner stopped, and starts it again afterwards.
    /// </summary>
    public Task WithHostedRunnerPausedAsync(Func<Task> body) => factory.WithWorkflowRunnersPausedAsync(body, Ct);

    public WorkflowRunner NewRunner(int? bound = null) => new(
        factory.Services,
        factory.Services.GetRequiredService<ILogger<WorkflowRunner>>(),
        bound is null
            ? factory.Services.GetRequiredService<IConfiguration>()
            : new ConfigurationBuilder()
                .AddConfiguration(factory.Services.GetRequiredService<IConfiguration>())
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [WorkflowRunner.ConcurrencyKey] = bound.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                })
                .Build());

    /// <summary>
    /// Runs a runner of the test's own until it finds nothing due, or until the test's own runs have
    /// nothing due.
    /// </summary>
    /// <remarks>
    /// The runner claims due work in every tenant, oldest first, and the database is shared with every
    /// other class, so older due runs of other tests are claimed before the test's own. Waiting for the
    /// whole database to go quiet is waiting on work that is not the test's. With the test's own runs
    /// named (or seeded through this harness) the drain ends once none of them is due. Without any it
    /// drains everything, as it always did.
    ///
    /// The poll limit is the loop detector. Past it, a drain that is still working through a backlog
    /// that is getting smaller goes on, and one whose due work is not shrinking fails, saying which
    /// workflows and actions the runner kept finding.
    /// </remarks>
    public async Task DrainAsync(params Guid[] runIds)
    {
        var own = runIds.Length > 0 ? runIds : [.. _seeded];
        var runner = NewRunner();

        var polls = 0;
        int? dueAtLastCheck = null;

        while (await runner.RunOnceAsync(Ct))
        {
            if (own.Length > 0 && !await AnyDueAsync(own))
            {
                return;
            }

            if (++polls % PollsPerCheck != 0)
            {
                continue;
            }

            var now = await DueSnapshotAsync();
            if (dueAtLastCheck is { } before && now.Total >= before)
            {
                Assert.Fail(
                    $"The runner should drain rather than find work forever, but it was still finding work after {polls} polls "
                  + $"and the {now.Total} runs due had not gone down since the last check. Due now: {now.Describe()}");
            }

            dueAtLastCheck = now.Total;
        }
    }

    private async Task<bool> AnyDueAsync(Guid[] runIds)
    {
        var now = DateTimeOffset.UtcNow;

        await using var session = Store.QuerySession();
        return await session.Query<WorkflowRun>()
            .Where(WorkflowExecutors.DueFor(WorkflowExecutors.BuiltIn, now))
            .Where(r => r.Id.IsOneOf(runIds))
            .AnyAsync(Ct);
    }

    /// <summary>What the built-in runner would be offered at this moment, in every tenant that has unfinished runs.</summary>
    private async Task<DueSnapshot> DueSnapshotAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var store = Store;
        var partitions = await barakoCMS.Infrastructure.Multitenancy.TenantPartitions.ListAsync(
            store, factory.Services.GetRequiredService<IConfiguration>(), WorkflowRunner.PartitionsWithWorkSql, Ct);

        var total = 0;
        var sample = new List<(string Tenant, WorkflowRun Run)>();

        foreach (var tenant in partitions)
        {
            await using var session = store.QuerySession(tenant);
            var due = session.Query<WorkflowRun>().Where(WorkflowExecutors.DueFor(WorkflowExecutors.BuiltIn, now));

            total += await due.CountAsync(Ct);
            sample.AddRange((await due.OrderBy(r => r.CreatedAt).Take(500).ToListAsync(Ct)).Select(r => (tenant, r)));
        }

        return new DueSnapshot(total, sample);
    }

    private sealed record DueSnapshot(int Total, List<(string Tenant, WorkflowRun Run)> Sample)
    {
        /// <summary>The due runs grouped by tenant, workflow and the action each is waiting on, most first.</summary>
        public string Describe()
        {
            var groups = Sample
                .GroupBy(d => (
                    d.Tenant,
                    d.Run.WorkflowDefinitionId,
                    d.Run.WorkflowName,
                    Action: d.Run.Actions.OrderBy(a => a.Ordinal)
                        .FirstOrDefault(a => a.Status is AttemptStatus.Pending or AttemptStatus.Running)?.ActionType ?? "(none)"))
                .OrderByDescending(g => g.Count())
                .Take(8)
                .Select(g => $"{g.Count()} x tenant '{g.Key.Tenant}' workflow {g.Key.WorkflowDefinitionId} '{g.Key.WorkflowName}' "
                           + $"action '{g.Key.Action}' (most tries so far {g.Max(d => d.Run.Actions.Count == 0 ? 0 : d.Run.Actions.Max(a => a.Attempts))})");

            return $"{Total} due in all, looked at the oldest {Sample.Count}: " + string.Join("; ", groups);
        }
    }
}
