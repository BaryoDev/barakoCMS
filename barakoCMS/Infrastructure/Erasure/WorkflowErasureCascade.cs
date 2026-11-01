using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Erasure;

/// <summary>
/// Removes what workflows copied out of an erased entry: its outbox messages, and the captured
/// outputs and error text on its runs.
/// </summary>
/// <remarks>
/// An outbox message body is the exact JSON that was or would be sent, so the row goes, and a
/// pending one cannot send afterwards. A run stays as history with its values blanked. Execution
/// logs and missed triggers hold only ids and event names and are left alone.
/// </remarks>
internal sealed class WorkflowErasureCascade : IContentErasureCascade
{
    public async Task QueueAsync(IDocumentSession session, Guid contentId, string contentType, CancellationToken ct)
    {
        session.DeleteWhere<OutboxMessage>(m => m.ContentId == contentId);

        var runs = await session.Query<WorkflowRun>().Where(r => r.ContentId == contentId).ToListAsync(ct);
        foreach (var run in runs)
        {
            foreach (var action in run.Actions)
            {
                action.Outputs = null;
                action.Error = null;
                action.History = action.History.Select(h => h with { Error = null }).ToList();
                if (action.Compensation is { } undo)
                {
                    undo.Error = null;
                }
            }

            session.Store(run);
        }
    }
}
