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

    /// <summary>When set, every call waits for it after it is recorded.</summary>
    public static volatile Task? Hold;

    public async Task OnRunFailedAsync(WorkflowRunFailure failure, CancellationToken ct)
    {
        Told.AddOrUpdate(failure.Run.Id, 1, (_, told) => told + 1);
        if (Hold is { } hold) await hold.WaitAsync(ct);
    }
}
