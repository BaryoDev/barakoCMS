using FluentAssertions;
using Xunit;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The deprecation window on <see cref="IWorkflowAction"/>.
/// </summary>
/// <remarks>
/// An action written against the older contract implements <c>ExecuteAsync</c> and nothing else.
/// That has to keep compiling and keep running for the whole of 4.x, which is what the default
/// implementation of <c>RunAsync</c> is for. <see cref="LegacyAction"/> below is the proof: it is
/// declared exactly as a module author would have written it before the result type existed, so if
/// the interface ever stops carrying such an action, this file stops compiling.
/// </remarks>
public class WorkflowActionContractTests
{
    [Fact]
    public async Task An_action_written_against_the_old_contract_still_runs_and_reports_success()
    {
        var action = new LegacyAction();

        var result = await ((IWorkflowAction)action).RunAsync(
            new Dictionary<string, string> { ["Key"] = "value" },
            new Content { Id = Guid.NewGuid(), ContentType = "Article" },
            CancellationToken.None);

        action.Ran.Should().BeTrue("the default implementation has to delegate, not no-op");
        result.Succeeded.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task An_action_written_against_the_old_contract_still_reports_failure_by_throwing()
    {
        var action = new ThrowingLegacyAction();

        var run = async () => await ((IWorkflowAction)action).RunAsync(
            new Dictionary<string, string>(),
            new Content { Id = Guid.NewGuid(), ContentType = "Article" },
            CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>(
            "the default implementation must not swallow the only way an older action can fail. "
          + "The engine catches it and records the failure");
    }

    [Fact]
    public void A_failure_carries_its_reason_and_a_success_does_not()
    {
        WorkflowActionResult.Success().Succeeded.Should().BeTrue();
        WorkflowActionResult.Success().Error.Should().BeNull();

        var failure = WorkflowActionResult.Failure("the target answered 500");
        failure.Succeeded.Should().BeFalse();
        failure.Error.Should().Be("the target answered 500");
    }

    private sealed class LegacyAction : IWorkflowAction
    {
        public bool Ran { get; private set; }

        public string Type => "Legacy";

        public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct)
        {
            Ran = true;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingLegacyAction : IWorkflowAction
    {
        public string Type => "ThrowingLegacy";

        public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct)
            => throw new InvalidOperationException("the provider rejected the recipient");
    }
}
