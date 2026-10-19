using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Workflows;

/// <summary>
/// Sets <see cref="WorkflowRun.UpdatedAt"/> on every run a session is about to write.
/// </summary>
/// <remarks>
/// On the store, so a run written by any path carries it: the runner, the retry and cancel
/// endpoints, the projection that queues one. Stamping at each call site would leave the next one
/// added unstamped, and a run that changed without moving its stamp is invisible to a client asking
/// what changed.
///
/// The stamp is taken before the transaction commits, so a slow transaction commits a stamp older
/// than ones already visible. The changed-since cursor lags by a margin for that reason. Whole
/// milliseconds, so a cursor made from a stored value compares equal to it in the database.
/// </remarks>
internal sealed class WorkflowRunStampListener : DocumentSessionListenerBase
{
    public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        Stamp(session);
        return Task.CompletedTask;
    }

    internal static DateTimeOffset Now()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
    }

    private static void Stamp(IDocumentSession session)
    {
        var runs = session.PendingChanges.AllChangedFor<WorkflowRun>().ToList();
        if (runs.Count == 0) return;

        var now = Now();
        foreach (var run in runs)
        {
            run.UpdatedAt = now;
        }
    }
}
