using System.Collections.Concurrent;
using barakoCMS.Modules;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Remembers the runs a failure handler was told about. Registered on <see cref="IntegrationTestFixture"/>
/// so the runner of any test, hosted or its own, reaches it.
/// </summary>
internal sealed class RecordingFailureHandler : IWorkflowRunFailureHandler
{
    public static readonly ConcurrentDictionary<Guid, int> Told = new();

    public Task OnRunFailedAsync(WorkflowRunFailure failure, CancellationToken ct)
    {
        Told.AddOrUpdate(failure.Run.Id, 1, (_, told) => told + 1);
        return Task.CompletedTask;
    }
}
