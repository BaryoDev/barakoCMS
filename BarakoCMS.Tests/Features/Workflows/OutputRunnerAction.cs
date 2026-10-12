using barakoCMS.Features.Workflows;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An action that returns outputs, the way a provider's response would come back: some a workflow
/// wants, one it must never keep, one too long to keep, and one that looks like a placeholder.
/// </summary>
/// <remarks>Registered on <see cref="IntegrationTestFixture"/>, since either hosted runner can claim it.</remarks>
internal sealed class OutputRunnerAction : IWorkflowAction
{
    public const string Secret = "sk_live_must_never_be_stored";

    public string Type => "OutputRunner";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        throw new NotSupportedException("The runner calls RunAsync(WorkflowContext).");

    public Task<WorkflowActionResult> RunAsync(WorkflowContext context, CancellationToken ct) =>
        Task.FromResult(WorkflowActionResult.Success(new Dictionary<string, string>
        {
            ["total"] = context.Parameters.GetValueOrDefault("Total") ?? "42",
            ["reference"] = $"ref-{context.Parameters.GetValueOrDefault("IdempotencyKey")}",
            ["secret"] = Secret,
            ["huge"] = new string('x', ActionOutputs.MaxValueLength + 1),
            ["inject"] = "{{id}}",
        }));
}

/// <summary>Records the parameters and earlier outputs each attempt was handed, by idempotency key.</summary>
internal sealed class RecordingRunnerAction : IWorkflowAction
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WorkflowContext> Seen = new();

    /// <summary>Every idempotency key in the order the runner handed it over.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Order = new();

    public string Type => "RecordingRunner";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        throw new NotSupportedException("The runner calls RunAsync(WorkflowContext).");

    public Task<WorkflowActionResult> RunAsync(WorkflowContext context, CancellationToken ct)
    {
        var key = context.Parameters.GetValueOrDefault("IdempotencyKey") ?? string.Empty;
        Seen[key] = context;
        Order.Enqueue(key);
        return Task.FromResult(WorkflowActionResult.Success());
    }
}
