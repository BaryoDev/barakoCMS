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
/// pending one cannot send afterwards. A run stays as history with its values blanked, and so do the
/// response bodies of its webhook deliveries. Execution
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

        // A delivery keeps the first bytes of what the provider answered, which can echo the entry.
        // The row stays as the record of the attempt.
        var runIds = runs.Select(r => r.Id).ToList();
        if (runIds.Count > 0)
        {
            var deliveries = await session.Query<WebhookDelivery>()
                .Where(d => d.RunId != null && runIds.Contains(d.RunId.Value))
                .ToListAsync(ct);
            foreach (var delivery in deliveries.Where(d => d.ResponseBody is not null))
            {
                delivery.ResponseBody = null;
                session.Store(delivery);
            }
        }
    }
}
