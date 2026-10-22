using System.Collections.Concurrent;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An action that fills <c>Raw</c> in itself and records what it was handed, by the <c>Tag</c>
/// parameter, so a test sees what the runner or a conditional passed rather than what was stored.
/// </summary>
/// <remarks>Registered on <see cref="IntegrationTestFixture"/>, since either hosted runner can claim it.</remarks>
[WorkflowActionMetadata(
    Description = "Records its parameters",
    OptionalParameters = new[] { "Raw", "Plain", "Tag" },
    SelfResolvedParameters = new[] { "Raw" })]
internal sealed class SelfResolvingRunnerAction : IWorkflowAction
{
    public static readonly ConcurrentDictionary<string, Dictionary<string, string>> ReceivedByTag = new();

    public string Type => "SelfResolving";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    public Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct)
    {
        ReceivedByTag[parameters.GetValueOrDefault("Tag") ?? string.Empty] = new Dictionary<string, string>(parameters);
        return Task.FromResult(WorkflowActionResult.Success());
    }
}
