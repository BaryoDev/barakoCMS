using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// The engine runs a matching workflow's actions and skips one whose conditions do not hold.
/// </summary>
/// <remarks>
/// This file used to contain `ProcessEventAsync_ShouldTriggerEmail_WhenConditionMet`, which had no
/// act and no assert. It built a workflow and a content item, wrote six comments about how hard
/// mocking Marten's query is, and ended on `await Task.CompletedTask`. It was an active Fact, it ran
/// on every build, and it could not fail.
///
/// The comments were right about the difficulty and wrong about the conclusion. Mocking
/// `session.Query` is genuinely awkward, which is the argument for driving the real engine against
/// the real store rather than for giving up. The fixture that makes that easy already existed.
/// </remarks>
[Collection("Sequential")]
public class WorkflowTests
{
    private readonly IntegrationTestFixture _factory;

    public WorkflowTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>Records what it was asked to do, so a test can assert the engine reached it.</summary>
    private sealed class SpyAction : barakoCMS.Features.Workflows.IWorkflowAction
    {
        public string Type => "Spy";
        public List<Dictionary<string, string>> Executions { get; } = new();

        public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct)
        {
            Executions.Add(new Dictionary<string, string>(parameters));
            return Task.CompletedTask;
        }
    }

    private async Task<(barakoCMS.Features.Workflows.IWorkflowEngine Engine, SpyAction Spy, IServiceScope Scope)> EngineAsync()
    {
        var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var spy = new SpyAction();

        var engine = new barakoCMS.Features.Workflows.WorkflowEngine(
            session,
            [spy],
            scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Services.ITemplateVariableExtractor>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<barakoCMS.Features.Workflows.WorkflowEngine>>());

        return (engine, spy, scope);
    }

    private static WorkflowDefinition Definition(string type, Dictionary<string, string> conditions) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"wf_{Guid.NewGuid():n}"[..12],
        TriggerContentType = type,
        TriggerEvent = "Created",
        Conditions = conditions,
        Actions = [new WorkflowAction { Type = "Spy", Parameters = new() { ["To"] = "someone@example.com" } }],
    };

    [Fact]
    public async Task A_matching_workflow_runs_its_actions()
    {
        var type = $"wft_{Guid.NewGuid():n}"[..12];
        var (engine, spy, scope) = await EngineAsync();
        using var _ = scope;

        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(Definition(type, new() { ["Status"] = "New" }));
        await session.SaveChangesAsync();

        await engine.ProcessEventAsync(type, "Created", new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Data = new Dictionary<string, object> { ["Status"] = "New" },
        }, CancellationToken.None);

        spy.Executions.Should().ContainSingle("the conditions match, so the action runs");
        spy.Executions[0]["To"].Should().Be("someone@example.com",
            "the action receives the parameters the workflow declared");
    }

    /// <summary>
    /// The half that makes the test above mean something.
    /// </summary>
    /// <remarks>
    /// An engine that ran every action regardless of conditions would satisfy the first test
    /// completely. Conditions are the entire feature, so not-running is as much the behaviour as
    /// running is.
    /// </remarks>
    [Fact]
    public async Task A_workflow_whose_conditions_do_not_hold_runs_nothing()
    {
        var type = $"wff_{Guid.NewGuid():n}"[..12];
        var (engine, spy, scope) = await EngineAsync();
        using var _ = scope;

        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(Definition(type, new() { ["Status"] = "New" }));
        await session.SaveChangesAsync();

        await engine.ProcessEventAsync(type, "Created", new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Data = new Dictionary<string, object> { ["Status"] = "Shipped" },
        }, CancellationToken.None);

        spy.Executions.Should().BeEmpty("Status is Shipped and the workflow wanted New");
    }

    /// <summary>
    /// An action that throws does not stop the later actions in the same workflow.
    /// </summary>
    /// <remarks>
    /// One workflow, two actions, the failing one first. That shape is the whole test: the guard
    /// being checked is the per-action catch inside ExecuteActionsAsync, and the only thing it
    /// changes is whether the actions after a failure still run.
    ///
    /// Two earlier drafts got this wrong the same way and both passed. They used two separate
    /// workflows, so removing the per-action catch changed nothing: the exception simply propagated
    /// to the per-workflow catch, which swallowed it, and the other workflow ran anyway. The outer
    /// guard masks the inner one completely unless the two actions share a workflow.
    ///
    /// Worth writing down, because the mistake is invisible in a passing test and it is the exact
    /// defect this file was opened to remove.
    /// </remarks>
    [Fact]
    public async Task A_failing_action_does_not_stop_the_later_actions_in_its_workflow()
    {
        var type = $"wfa_{Guid.NewGuid():n}"[..12];
        var scope = _factory.Services.CreateScope();
        using var _ = scope;

        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var spy = new SpyAction();
        var engine = new barakoCMS.Features.Workflows.WorkflowEngine(
            session,
            [spy, new ThrowingAction()],
            scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Services.ITemplateVariableExtractor>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<barakoCMS.Features.Workflows.WorkflowEngine>>());

        var workflow = Definition(type, new());
        workflow.Actions =
        [
            new WorkflowAction { Type = "Boom", Parameters = new() },
            new WorkflowAction { Type = "Spy", Parameters = new() { ["To"] = "after@example.com" } },
        ];
        session.Store(workflow);
        await session.SaveChangesAsync();

        await engine.ProcessEventAsync(type, "Created", new Content
        {
            Id = Guid.NewGuid(), ContentType = type, Data = new Dictionary<string, object>(),
        }, CancellationToken.None);

        spy.Executions.Should().ContainSingle(
            "the action after the failing one still runs, which is the only thing the per-action "
          + "catch changes");
    }

    /// <summary>
    /// A workflow that fails before any action runs does not stop the other workflows.
    /// </summary>
    /// <remarks>
    /// The outer guard, and the one that actually matters for the daemon. `ProcessEventAsync` runs
    /// inside the async projection, where an escaped exception halts the projection and silently
    /// stops every workflow in the system until a manual rebuild that does not exist (#285).
    ///
    /// Reaching it needs a failure before `ExecuteActionsAsync` swallows things, so this uses a
    /// condition the evaluator cannot handle rather than a throwing action. That is the only route
    /// between the two catches, which is worth knowing: it is a narrow gap and the guard is there
    /// for what falls through it.
    /// </remarks>
    [Fact]
    public async Task A_workflow_that_fails_before_its_actions_does_not_stop_the_others()
    {
        var type = $"wfo_{Guid.NewGuid():n}"[..12];
        var (engine, spy, scope) = await EngineAsync();
        using var _ = scope;

        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var broken = Definition(type, new());
        broken.Conditions = null!; // MatchesConditions enumerates this, outside the per-action catch.
        session.Store(broken);
        session.Store(Definition(type, new()));
        await session.SaveChangesAsync();

        var act = async () => await engine.ProcessEventAsync(type, "Created", new Content
        {
            Id = Guid.NewGuid(), ContentType = type, Data = new Dictionary<string, object>(),
        }, CancellationToken.None);

        await act.Should().NotThrowAsync("an escape here halts the projection for everybody");
        spy.Executions.Should().ContainSingle("the healthy workflow still ran");
    }

    private sealed class ThrowingAction : barakoCMS.Features.Workflows.IWorkflowAction
    {
        public string Type => "Boom";

        public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
            throw new InvalidOperationException("this action always fails");
    }
}
