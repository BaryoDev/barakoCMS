using System.Collections.Concurrent;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An action that counts its runs by idempotency key and does nothing with the key, like Email.
/// </summary>
/// <remarks>
/// Registered on <see cref="IntegrationTestFixture"/> for the same reason as
/// <see cref="CountingRunnerAction"/>: either hosted runner can claim the attempt. A second run for
/// one key is a second send, which is what a taken-over attempt of this type must not do.
/// </remarks>
internal sealed class SendOnceRunnerAction : barakoCMS.Features.Workflows.IWorkflowAction
{
    public static readonly ConcurrentDictionary<string, int> RunsByKey = new();

    public string Type => "SendOnceRunner";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    public Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunAsync(
        Dictionary<string, string> parameters, Content content, CancellationToken ct)
    {
        RunsByKey.AddOrUpdate(parameters.GetValueOrDefault("IdempotencyKey") ?? string.Empty, 1, (_, runs) => runs + 1);
        return Task.FromResult(barakoCMS.Features.Workflows.WorkflowActionResult.Success());
    }
}
