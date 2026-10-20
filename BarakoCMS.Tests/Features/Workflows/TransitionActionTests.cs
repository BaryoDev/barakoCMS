using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

public class TransitionActionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Content Entry() => new() { Id = Guid.NewGuid(), ContentType = "invoice" };

    private static (TransitionAction Action, IContentTransitioner Transitioner) Build(ContentTransitionResult result)
    {
        var transitioner = Substitute.For<IContentTransitioner>();
        transitioner.TransitionAsync(default!, default!, default!, default, default).ReturnsForAnyArgs(result);
        return (new TransitionAction(transitioner, NullLogger<TransitionAction>.Instance), transitioner);
    }

    [Fact]
    public async Task A_named_transition_runs_on_the_entry_in_scope_as_the_workflow_actor()
    {
        var (action, transitioner) = Build(ContentTransitionResult.Transitioned("approve", "Submitted", "Approved"));
        var entry = Entry();

        var result = await action.RunAsync(
            new Dictionary<string, string> { ["Transition"] = "approve", ["Data.Reason"] = "ok" }, entry, Ct);

        result.Succeeded.Should().BeTrue(result.Error);
        result.Outputs["ToState"].Should().Be("Approved");
        var calls = transitioner.ReceivedCalls().ToList();
        calls.Should().HaveCount(1);
        var args = calls[0].GetArguments();
        args[0].Should().BeSameAs(entry);
        args[1].Should().Be("approve");
        ((ContentTransitionActor)args[2]!).SystemName.Should().Be("workflow");
        var options = (ContentTransitionOptions)args[3]!;
        options.SkipPermissionChecks.Should().BeTrue();
        options.Data.Should().ContainKey("Reason").WhoseValue.Should().Be("ok");
    }

    [Fact]
    public async Task A_refused_permission_fails_the_step_without_a_retry()
    {
        var (action, _) = Build(ContentTransitionResult.Forbidden("The actor may not approve."));

        var result = await action.RunAsync(new Dictionary<string, string> { ["Transition"] = "approve" }, Entry(), Ct);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("The actor may not approve.");
    }

    [Fact]
    public async Task An_unknown_transition_fails_the_step_without_a_retry()
    {
        var (action, _) = Build(ContentTransitionResult.Invalid(["No transition named 'archive'."]));

        var result = await action.RunAsync(new Dictionary<string, string> { ["Transition"] = "archive" }, Entry(), Ct);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("No transition named 'archive'.");
    }

    [Fact]
    public async Task A_conflict_fails_the_step_and_may_be_retried()
    {
        var (action, _) = Build(ContentTransitionResult.Conflict("changed by another writer"));

        var result = await action.RunAsync(new Dictionary<string, string> { ["Transition"] = "approve" }, Entry(), Ct);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_transition_name_fails_before_the_transitioner_is_called()
    {
        var (action, transitioner) = Build(ContentTransitionResult.Transitioned("x", "a", "b"));

        var result = await action.RunAsync(new Dictionary<string, string>(), Entry(), Ct);

        result.Succeeded.Should().BeFalse();
        transitioner.ReceivedCalls().Should().BeEmpty();
    }
}
