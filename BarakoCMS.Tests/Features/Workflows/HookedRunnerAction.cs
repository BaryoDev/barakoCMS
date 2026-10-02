using System.Collections.Concurrent;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An action that calls back into the test while it is running, then fails in a way the runner
/// would queue again.
/// </summary>
/// <remarks>
/// The only moment a test can act between a claim and its outcome is inside the action. The hook is
/// keyed by run, and the failure is the retryable kind so the test can see whether the runner
/// queued the attempt again. Registered on <see cref="IntegrationTestFixture"/> for the same reason
/// as <see cref="CountingRunnerAction"/>: either hosted runner can claim the attempt.
/// </remarks>
internal sealed class HookedRunnerAction : barakoCMS.Features.Workflows.IWorkflowAction
{
    public static readonly ConcurrentDictionary<string, Func<Task>> DuringRun = new();

    public static readonly ConcurrentDictionary<string, int> RunsByRun = new();

    public string Type => "HookedRunner";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    public async Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunAsync(
        Dictionary<string, string> parameters, Content content, CancellationToken ct)
    {
        var runId = parameters.GetValueOrDefault("RunId") ?? string.Empty;
        RunsByRun.AddOrUpdate(runId, 1, (_, runs) => runs + 1);

        if (DuringRun.TryGetValue(runId, out var hook))
        {
            await hook();
        }

        return barakoCMS.Features.Workflows.WorkflowActionResult.Failure("the provider answered 503");
    }
}
