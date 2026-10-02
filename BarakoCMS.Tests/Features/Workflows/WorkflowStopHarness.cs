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
    public async Task<Guid> StoreContentAsync()
    {
        var id = Guid.NewGuid();

        await using var session = Store.LightweightSession();
        session.Store(new Content
        {
            Id = id,
            ContentType = "article",
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object>(),
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
        Guid workflowId, Guid contentId, IReadOnlyList<WorkflowActionAttempt> attempts, DateTimeOffset? cancelledAt = null)
    {
        var run = NewRun(workflowId, contentId, attempts, cancelledAt);

        await using var session = Store.LightweightSession();
        session.Store(run);
        await session.SaveChangesAsync(Ct);

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
    public async Task WithHostedRunnerPausedAsync(Func<Task> body)
    {
        var hosted = factory.Services.GetServices<IHostedService>().OfType<WorkflowRunner>().Single();

        await hosted.StopAsync(Ct);

        try
        {
            await body();
        }
        finally
        {
            // Not the test's token: the runner's loop is linked to the one it is started with, and
            // this one is cancelled when the test ends.
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    /// <summary>Runs a runner of the test's own until it finds nothing due.</summary>
    public async Task DrainAsync()
    {
        var runner = new WorkflowRunner(
            factory.Services,
            factory.Services.GetRequiredService<ILogger<WorkflowRunner>>(),
            factory.Services.GetRequiredService<IConfiguration>());

        var polls = 0;
        while (await runner.RunOnceAsync(Ct))
        {
            (++polls).Should().BeLessThan(200, "the runner should drain rather than find work forever");
        }
    }
}
