using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An action that ends the way its <see cref="ModeParameter"/> says.
/// </summary>
/// <remarks>
/// Only <see cref="WorkflowRunnerMetricsTests"/> queues it, so a metric series labelled with this
/// type moves for that class's runs and for nothing a test elsewhere left behind. Registered on
/// <see cref="IntegrationTestFixture"/> for the same reason as <see cref="CountingRunnerAction"/>.
/// </remarks>
internal sealed class MeteredRunnerAction : barakoCMS.Features.Workflows.IWorkflowAction
{
    public const string ActionType = "MeteredRunner";

    public const string ModeParameter = "Mode";

    public const string Succeed = "succeed";

    /// <summary>A failure the runner queues again.</summary>
    public const string Fail = "fail";

    /// <summary>A failure the runner does not retry.</summary>
    public const string Refuse = "refuse";

    /// <summary>A timeout, which the runner records as Unknown.</summary>
    public const string TimeOut = "timeout";

    public string Type => ActionType;

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    public Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunAsync(
        Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        parameters.GetValueOrDefault(ModeParameter) switch
        {
            Fail => Task.FromResult(barakoCMS.Features.Workflows.WorkflowActionResult.Failure("the provider answered 503")),
            Refuse => Task.FromResult(barakoCMS.Features.Workflows.WorkflowActionResult.PermanentFailure("the address cannot be called")),
            TimeOut => throw new TaskCanceledException("the provider did not answer"),
            _ => Task.FromResult(barakoCMS.Features.Workflows.WorkflowActionResult.Success()),
        };
}
