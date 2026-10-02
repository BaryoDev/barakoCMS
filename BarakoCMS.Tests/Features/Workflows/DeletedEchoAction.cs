using System.Collections.Concurrent;
using System.Text.Json;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An action that records the content and the parameters it was handed, keyed by run, so a test can
/// see what the runner gave an action for an erased entry.
/// </summary>
/// <remarks>
/// Registered on <see cref="IntegrationTestFixture"/> for the same reason as
/// <see cref="CredentialEchoAction"/>: either runner can claim the attempt, and the capture is
/// static so it does not matter which one did.
/// </remarks>
internal sealed class DeletedEchoAction : barakoCMS.Features.Workflows.IWorkflowAction
{
    internal sealed record Received(Guid ContentId, string ContentType, string ContentJson, string ParametersJson);

    public static readonly ConcurrentDictionary<string, Received> ReceivedByRun = new();

    public string Type => "DeletedEcho";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    public Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunAsync(
        Dictionary<string, string> parameters, Content content, CancellationToken ct)
    {
        ReceivedByRun[parameters.GetValueOrDefault("RunId") ?? string.Empty] = new Received(
            content.Id, content.ContentType, JsonSerializer.Serialize(content), JsonSerializer.Serialize(parameters));
        return Task.FromResult(barakoCMS.Features.Workflows.WorkflowActionResult.Success());
    }
}
