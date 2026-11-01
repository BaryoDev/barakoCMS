using barakoCMS.Models;
using JasperFx.Events;
using Marten;

namespace barakoCMS.Features.Workflows;

/// <summary>
/// Replays the events <see cref="WorkflowProjection"/> could not queue runs for, one tenant at a time.
/// </summary>
/// <remarks>
/// <para>
/// A row is deleted once its replay returns, whether it queued a run or found nothing to queue:
/// the entry or the event is gone, or no workflow matches any more. A replay that throws keeps the
/// row and counts an attempt, and the tenant's other rows are still tried.
/// </para>
/// <para>
/// A crash between the run's save and the row's delete replays the row again, and the queue's
/// check for a run at the same sequence queues nothing the second time.
/// </para>
/// </remarks>
internal static class MissedTriggerSweep
{
    /// <summary>How many rows one sweep of one tenant takes.</summary>
    internal const int BatchSize = 100;

    /// <summary>Replays after which a row is left alone, so one poisoned row is not retried every minute for ever.</summary>
    internal const int MaxAttempts = 10;

    /// <returns>How many rows were replayed and removed.</returns>
    internal static async Task<int> RunAsync(
        IDocumentSession session, IWorkflowRunQueue queue, ILogger? logger, CancellationToken ct)
    {
        var rows = await session.Query<MissedWorkflowTrigger>()
            .Where(r => r.Attempts < MaxAttempts)
            .OrderBy(r => r.RecordedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        var handled = 0;
        foreach (var row in rows)
        {
            try
            {
                await ReplayAsync(session, queue, row, ct);
                session.Delete(row);
                await session.SaveChangesAsync(ct);
                handled++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                session.EjectAllPendingChanges();
                logger?.LogWarning(ex, "Replaying the missed {EventType} trigger for content {ContentId} failed; it is tried again on the next sweep",
                    row.EventType, row.ContentId);
                await CountAttemptAsync(session, row, ex, logger, ct);
            }
        }

        return handled;
    }

    private static async Task CountAttemptAsync(
        IDocumentSession session, MissedWorkflowTrigger row, Exception cause, ILogger? logger, CancellationToken ct)
    {
        try
        {
            row.Attempts++;
            row.LastError = cause.GetType().Name;
            session.Store(row);
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            session.EjectAllPendingChanges();
            logger?.LogWarning(ex, "Could not count a failed replay of the missed {EventType} trigger for content {ContentId}",
                row.EventType, row.ContentId);
        }
    }

    /// <summary>Queues again what the projection would have queued, from the stored event.</summary>
    /// <remarks>
    /// The event is read back by its sequence because Created and Updated are matched against the
    /// data the event wrote, not the entry as it is now (see <see cref="WorkflowProjection.AsWritten"/>),
    /// and because it carries the origin a run is parented to. An event that cannot be read still
    /// queues the other triggers from the entry as stored, which is all the projection reads for them.
    /// An Unpublished trigger needs the event to tell whether the entry was published before it,
    /// and queues nothing without it.
    /// </remarks>
    private static async Task ReplayAsync(
        IDocumentSession session, IWorkflowRunQueue queue, MissedWorkflowTrigger row, CancellationToken ct)
    {
        var stored = await session.LoadAsync<barakoCMS.Models.Content>(row.ContentId, ct);
        if (stored is null) return;

        var raw = await session.Events.QueryAllRawEvents().Where(e => e.Sequence == row.Sequence).FirstOrDefaultAsync(ct);
        using var origin = raw is null ? null : WorkflowProjection.OriginOf(raw);

        var content = raw switch
        {
            IEvent<barakoCMS.Events.ContentUpdated> updated =>
                WorkflowProjection.AsWritten(stored, updated.Data.Data, stored.Status),
            IEvent<barakoCMS.Events.ContentCreated> created =>
                WorkflowProjection.AsWritten(stored, created.Data.Data, created.Data.Status),
            _ => stored,
        };

        if (row.EventType == WorkflowEvents.Unpublished
            && !await WasUnpublishedAsync(session, queue, raw, stored, ct))
        {
            return;
        }

        await queue.EnqueueAsync(content, row.EventType, row.Sequence, ct);
    }

    private static async Task<bool> WasUnpublishedAsync(
        IDocumentSession session, IWorkflowRunQueue queue, IEvent? raw, barakoCMS.Models.Content stored, CancellationToken ct) =>
        raw is IEvent<barakoCMS.Events.ContentStatusChanged> changed
        && await queue.ListensAsync(stored.ContentType, WorkflowEvents.Unpublished, ct)
        && await WorkflowProjection.WasPublishedBeforeAsync(changed, session, ct) == true;
}
