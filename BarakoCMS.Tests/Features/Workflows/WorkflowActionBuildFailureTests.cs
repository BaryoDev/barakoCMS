using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>An action whose constructor always throws, with a setting-shaped value in the message.</summary>
internal sealed class BrokenConstructorAction : IWorkflowAction
{
    public const string Secret = "Password=hunter2-1111";

    public BrokenConstructorAction() => throw new InvalidOperationException($"could not read {Secret}");

    public string Type => "BrokenConstructor";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        Task.FromResult(WorkflowActionResult.Success());
}

/// <summary>
/// One registered action whose constructor throws fails only the steps that use it (#1111).
/// </summary>
/// <remarks>
/// Building every registered action was one resolution, outside the attempt's try, so one action
/// that could not be built failed every step of every run. The broken action is registered on a host
/// of this class's own, and the fixture's hosted runner is paused, so the runner built here on that
/// host is the only one that claims these runs.
/// </remarks>
[Collection("Sequential")]
public class WorkflowActionBuildFailureTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly WorkflowStopHarness _harness;

    public WorkflowActionBuildFailureTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _harness = new WorkflowStopHarness(fixture);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _host;

    private WebApplicationFactory<Program> Host()
    {
        lock (Gate)
        {
            return _host ??= _fixture.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
                services.AddScoped<IWorkflowAction, BrokenConstructorAction>()));
        }
    }

    private async Task<Guid> SeedAsync(string actionType, int attempts = 0, Dictionary<string, string>? parameters = null)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using var session = store.LightweightSession();
        session.Store(new Content
        {
            Id = contentId,
            ContentType = "article",
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object>(),
        });

        var run = new WorkflowRun
        {
            Id = runId,
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = "Action build failure",
            // The runner looks at the oldest unfinished runs first (#695), so this one is seen.
            CreatedAt = DateTimeOffset.UnixEpoch,
            ContentId = contentId,
            ContentType = "article",
            TriggerEvent = WorkflowEvents.Published,
            TriggeringEventSequence = 1,
            Actions =
            [
                new WorkflowActionAttempt
                {
                    Ordinal = 0,
                    ActionType = actionType,
                    Attempts = attempts,
                    IdempotencyKey = $"{runId:N}-0",
                    Parameters = parameters ?? [],
                },
            ],
        };
        run.Recompute();
        session.Store(run);
        await session.SaveChangesAsync(Ct);

        return runId;
    }

    private async Task<WorkflowRun> DrainAsync(Guid runId)
    {
        var services = Host().Services;
        var runner = new WorkflowRunner(
            services,
            services.GetRequiredService<ILogger<WorkflowRunner>>(),
            services.GetRequiredService<IConfiguration>());

        var polls = 0;
        while (await runner.RunOnceAsync(Ct))
        {
            (++polls).Should().BeLessThan(200, "the runner should drain rather than find work forever");
        }

        return await _harness.LoadRunAsync(runId);
    }

    [Fact]
    public void The_host_really_has_an_action_that_cannot_be_built()
    {
        using var scope = Host().Services.CreateScope();

        var build = () => scope.ServiceProvider.GetServices<IWorkflowAction>().ToList();

        build.Should().Throw<InvalidOperationException>(
            "otherwise the tests below pass without exercising a broken action at all");
    }

    [Fact]
    public async Task A_run_whose_steps_use_only_other_actions_succeeds()
    {
        WorkflowRun run = null!;
        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var runId = await SeedAsync(WorkflowStopHarness.Counting);
            run = await DrainAsync(runId);
        });

        run.Actions.Should().HaveCount(1);
        run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded, run.Actions[0].Error);
    }

    [Fact]
    public async Task A_step_using_the_broken_action_fails_with_a_recorded_error_on_the_normal_attempt_limit()
    {
        WorkflowRun run = null!;
        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var runId = await SeedAsync("BrokenConstructor", attempts: WorkflowRetryPolicy.MaxAttempts - 1);
            run = await DrainAsync(runId);
        });

        run.Actions.Should().HaveCount(1);
        var attempt = run.Actions[0];
        attempt.Status.Should().Be(AttemptStatus.Failed);
        attempt.Attempts.Should().Be(WorkflowRetryPolicy.MaxAttempts, "the last allowed attempt ends it");
        attempt.Retryable.Should().BeTrue("a constructor that failed on a setting can work once the setting is fixed");
        attempt.Error.Should().Contain("BrokenConstructorAction (InvalidOperationException)");
        attempt.Error.Should().NotContain(BrokenConstructorAction.Secret, "the constructor's message can hold a setting");
    }

    [Fact]
    public async Task A_first_failure_to_build_waits_for_a_retry_instead_of_looping()
    {
        WorkflowRun run = null!;
        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var runId = await SeedAsync("BrokenConstructor");
            run = await DrainAsync(runId);
        });

        run.Actions.Should().HaveCount(1);
        var attempt = run.Actions[0];
        attempt.Status.Should().Be(AttemptStatus.Pending);
        attempt.Attempts.Should().Be(1, "one attempt, then the backoff");
        attempt.NextAttemptAt.Should().NotBeNull().And.BeAfter(DateTimeOffset.UtcNow.AddMilliseconds(-1));
    }

    [Fact]
    public async Task A_conditional_whose_children_use_only_other_actions_succeeds()
    {
        var parameters = new Dictionary<string, string>
        {
            ["Condition"] = "{{status}} == Published",
            ["ThenActions"] = JsonSerializer.Serialize(new[] { new { Type = WorkflowStopHarness.Counting, Parameters = new Dictionary<string, string>() } }),
            ["ElseActions"] = "[]",
        };

        WorkflowRun run = null!;
        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var runId = await SeedAsync("Conditional", parameters: parameters);
            run = await DrainAsync(runId);
        });

        run.Actions.Should().HaveCount(1);
        run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded, run.Actions[0].Error);
    }
}
