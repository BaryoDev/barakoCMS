using System.Collections.Concurrent;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An action that counts how many times it ran for each idempotency key.
/// </summary>
/// <remarks>
/// The run record cannot show a duplicate execution: the second outcome is discarded, so the
/// attempt count reads one either way. Registered on <see cref="IntegrationTestFixture"/> for the
/// same reason as <see cref="ThrowingRunnerAction"/>: either hosted runner can claim the attempt.
/// </remarks>
internal sealed class CountingRunnerAction : barakoCMS.Features.Workflows.IWorkflowAction
{
    public static readonly ConcurrentDictionary<string, int> RunsByKey = new();

    public string Type => "CountingRunner";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    public Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunAsync(
        Dictionary<string, string> parameters, Content content, CancellationToken ct)
    {
        RunsByKey.AddOrUpdate(parameters.GetValueOrDefault("IdempotencyKey") ?? string.Empty, 1, (_, runs) => runs + 1);
        return Task.FromResult(barakoCMS.Features.Workflows.WorkflowActionResult.Success());
    }
}
