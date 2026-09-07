using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An action that always throws, with a secret-shaped string in its message.
/// </summary>
/// <remarks>
/// Registered on <see cref="IntegrationTestFixture"/> rather than on a host one test builds,
/// because two hosted runners poll the same database and either can claim an attempt first.
/// Registered on one host only, a test asserting on the recorded error passes or fails on which
/// runner won: the other one records "No handler is registered for action type" instead.
/// </remarks>
internal sealed class ThrowingRunnerAction : barakoCMS.Features.Workflows.IWorkflowAction
{
    public string Type => "ThrowingRunner";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        throw new InvalidOperationException("provider rejected sk_live_1234567890");

    public Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunAsync(
        Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        throw new InvalidOperationException("provider rejected sk_live_1234567890");
}
