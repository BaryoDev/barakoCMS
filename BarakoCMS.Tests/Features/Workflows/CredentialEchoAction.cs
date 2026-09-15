using System.Collections.Concurrent;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An action that records the ApiKey it was handed, keyed by run, so a test can see what the runner
/// passed an action rather than what was stored.
/// </summary>
/// <remarks>
/// Registered on <see cref="IntegrationTestFixture"/> for the same reason as
/// <see cref="ThrowingRunnerAction"/>: either hosted runner can claim the attempt. The capture is
/// static so it does not matter which one did.
/// </remarks>
internal sealed class CredentialEchoAction : barakoCMS.Features.Workflows.IWorkflowAction
{
    public static readonly ConcurrentDictionary<string, string?> ReceivedByRun = new();

    public string Type => "CredentialEcho";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    public Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunAsync(
        Dictionary<string, string> parameters, Content content, CancellationToken ct)
    {
        ReceivedByRun[parameters.GetValueOrDefault("RunId") ?? string.Empty] = parameters.GetValueOrDefault("ApiKey");
        return Task.FromResult(barakoCMS.Features.Workflows.WorkflowActionResult.Success());
    }
}
