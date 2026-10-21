using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>The entries an action reports it created are kept on its attempt, and only on a success.</summary>
/// <remarks>Runs are seeded and drained with the hosted runner stopped, as in <see cref="WorkflowOutputTests"/>.</remarks>
[Collection("Sequential")]
public class CreatedRefTests
{
    private readonly WorkflowStopHarness _harness;

    public CreatedRefTests(IntegrationTestFixture factory) => _harness = new WorkflowStopHarness(factory);

    [Fact]
    public void Success_with_created_entries_exposes_them()
    {
        var id = Guid.CreateVersion7();

        var result = WorkflowActionResult.Success(null, [new CreatedRef("order", id, "BR1-0001")]);

        result.Succeeded.Should().BeTrue();
        result.Created.Should().ContainSingle().Which.Should().Be(new CreatedRef("order", id, "BR1-0001"));
        WorkflowActionResult.Success().Created.Should().BeNull();
    }

    [Fact]
    public async Task A_succeeded_attempt_keeps_the_entries_its_action_created()
    {
        var run = await DrainAsync(Creating(fail: false));

        run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded);
        run.Actions[0].Created.Should().ContainSingle().Which.Number.Should().Be("BR1-0001");
    }

    [Fact]
    public async Task A_failed_attempt_keeps_none()
    {
        var run = await DrainAsync(Creating(fail: true));

        run.Actions[0].Status.Should().NotBe(AttemptStatus.Succeeded);
        run.Actions[0].Created.Should().BeNull();
    }

    private static WorkflowActionAttempt Creating(bool fail)
    {
        var attempt = WorkflowStopHarness.Waiting("CreatingRunner", due: true);
        attempt.Parameters = new() { ["Fail"] = fail ? "true" : "false" };
        return attempt;
    }

    private async Task<WorkflowRun> DrainAsync(WorkflowActionAttempt attempt)
    {
        var contentId = await _harness.StoreContentAsync();
        var workflowId = await _harness.StoreWorkflowAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var seeded = await _harness.SeedRunAsync(workflowId, contentId, [attempt]);
            await _harness.DrainAsync();
            run = await _harness.LoadRunAsync(seeded.Id);
        });

        return run;
    }
}

/// <summary>Reports one created entry, or fails for good when its Fail parameter is true.</summary>
internal sealed class CreatingRunnerAction : IWorkflowAction
{
    public string Type => "CreatingRunner";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        throw new NotSupportedException("The runner calls RunAsync(WorkflowContext).");

    public Task<WorkflowActionResult> RunAsync(WorkflowContext context, CancellationToken ct) =>
        Task.FromResult(context.Parameters.GetValueOrDefault("Fail") == "true"
            ? WorkflowActionResult.PermanentFailure("Told to fail.")
            : WorkflowActionResult.Success(null, [new CreatedRef("order", Guid.CreateVersion7(), "BR1-0001")]));
}
