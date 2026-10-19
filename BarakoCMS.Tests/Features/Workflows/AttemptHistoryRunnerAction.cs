using System.Collections.Concurrent;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An action that fails a set number of times, then succeeds, with an error text the test chooses.
/// </summary>
/// <remarks>
/// The count and the text come from the attempt's parameters, and the count is kept per run so a
/// retry sees how many times this run has already failed. Registered on
/// <see cref="IntegrationTestFixture"/> because either hosted runner can claim the attempt.
/// </remarks>
internal sealed class AttemptHistoryRunnerAction : barakoCMS.Features.Workflows.IWorkflowAction
{
    public const string ActionType = "AttemptHistoryRunner";

    private static readonly ConcurrentDictionary<string, int> Failed = new();

    public string Type => ActionType;

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    public Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunAsync(
        Dictionary<string, string> parameters, Content content, CancellationToken ct)
    {
        var runId = parameters.GetValueOrDefault("RunId") ?? string.Empty;
        var failTimes = int.Parse(parameters.GetValueOrDefault("FailTimes") ?? "0");

        var failedSoFar = Failed.AddOrUpdate(runId, 0, (_, n) => n);
        if (failedSoFar >= failTimes)
        {
            return Task.FromResult(barakoCMS.Features.Workflows.WorkflowActionResult.Success()
                with { ResponseStatus = 200 });
        }

        Failed[runId] = failedSoFar + 1;
        return Task.FromResult(barakoCMS.Features.Workflows.WorkflowActionResult
            .Failure(parameters.GetValueOrDefault("Error") ?? "the provider answered 503")
            with { ResponseStatus = 503 });
    }
}

/// <summary>
/// Runs a test's code at the moment the runner saves an attempt's outcome, so a second writer lands
/// between the runner's load and its save.
/// </summary>
/// <remarks>
/// Fires once per run, for a run that is about to be saved with a history entry on one of its
/// attempts. The hook is removed before it runs, so its own save passes through unhindered.
/// </remarks>
internal sealed class RunnerSaveRaceListener : DocumentSessionListenerBase
{
    public static readonly ConcurrentDictionary<Guid, Func<Task>> BeforeSave = new();

    public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        if (BeforeSave.IsEmpty) return;

        foreach (var run in session.PendingChanges.AllChangedFor<WorkflowRun>())
        {
            if (run.Actions.Any(a => a.History.Count > 0) && BeforeSave.TryRemove(run.Id, out var hook))
            {
                await hook();
            }
        }
    }
}
