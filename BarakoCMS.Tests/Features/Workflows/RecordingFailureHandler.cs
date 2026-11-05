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

    /// <summary>
    /// When set, the call for that run waits for the task after it is recorded. Only that run: a failed
    /// run from another test reaching the same handler must not block too.
    /// </summary>
    public static volatile HeldRun? Hold;

    public async Task OnRunFailedAsync(WorkflowRunFailure failure, CancellationToken ct)
    {
        Told.AddOrUpdate(failure.Run.Id, 1, (_, told) => told + 1);
        if (Hold is { } hold && hold.RunId == failure.Run.Id) await hold.Gate.WaitAsync(ct);
    }
}

internal sealed record HeldRun(Guid RunId, Task Gate);
