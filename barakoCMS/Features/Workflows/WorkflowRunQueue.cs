using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows;

/// <summary>Whether a workflow's conditions match the content that triggered it.</summary>
/// <remarks>
/// Shared by the queue, which decides what to enqueue, and the dry run, which shows what would
/// happen. Two copies of this logic would answer differently the first time one of them was fixed,
/// and a dry run that disagrees with the real thing is worse than no dry run.
/// </remarks>
internal static class WorkflowConditions
{
    internal static bool Matches(WorkflowDefinition workflow, barakoCMS.Models.Content content)
    {
        foreach (var condition in workflow.Conditions)
        {
            // Status is answered from the document before the data bag is consulted, and the order
            // is the whole of it. An entry with its own "Status" field, which is an ordinary thing to
            // model, used to shadow the lifecycle status: a workflow conditioned on Status Published
            // then fired on whatever that field happened to say. Nothing named the system property,
            // so the two were indistinguishable from inside the rule.
            if (condition.Key == "Status")
            {
                if (content.Status.ToString() != condition.Value) return false;
            }
            else if (content.Data.TryGetValue(condition.Key, out var value))
            {
                if (value?.ToString() != condition.Value) return false;
            }
            else
            {
                return false;
            }
        }

        return true;
    }
}

/// <remarks>
/// Internal, like everything else under Features. The projection is the only caller and it lives in
/// the same assembly; making it public would freeze this shape as contract under section 6, which
/// nobody asked for and could not be withdrawn until the next major.
/// </remarks>
internal interface IWorkflowRunQueue
{
    /// <summary>
    /// Records what should happen for one event, without doing any of it.
    /// </summary>
    /// <returns>How many runs were queued.</returns>
    Task<int> EnqueueAsync(barakoCMS.Models.Content content, string eventType, long eventSequence, CancellationToken ct);
}

internal sealed class WorkflowRunQueue : IWorkflowRunQueue
{
    private readonly IDocumentSession _session;
    private readonly ILogger<WorkflowRunQueue> _logger;

    public WorkflowRunQueue(IDocumentSession session, ILogger<WorkflowRunQueue> logger)
    {
        _session = session;
        _logger = logger;
    }

    public async Task<int> EnqueueAsync(barakoCMS.Models.Content content, string eventType, long eventSequence, CancellationToken ct)
    {
        var workflows = await _session.Query<WorkflowDefinition>()
            .Where(w => w.TriggerContentType == content.ContentType && w.TriggerEvent == eventType)
            .ToListAsync(ct);

        if (workflows.Count == 0) return 0;

        var queued = 0;

        foreach (var workflow in workflows)
        {
            if (!WorkflowConditions.Matches(workflow, content)) continue;

            // Already queued for this exact event, so a projection rebuild does not re-send
            // everything. A rebuild replays every event ever stored, and without this the first one
            // would re-fire every email and webhook this instance has ever sent. That is the failure
            // docs/operating-workflows.md calls expensive, and it stops being possible here.
            var already = await _session.Query<WorkflowRun>()
                .Where(r => r.WorkflowDefinitionId == workflow.Id
                            && r.ContentId == content.Id
                            && r.TriggeringEventSequence == eventSequence)
                .AnyAsync(ct);

            if (already)
            {
                _logger.LogDebug(
                    "Workflow {WorkflowId} already has a run for content {ContentId} at sequence {Sequence}",
                    workflow.Id, content.Id, eventSequence);
                continue;
            }

            var run = new WorkflowRun
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflow.Id,
                WorkflowName = workflow.Name,
                ContentId = content.Id,
                ContentType = content.ContentType,
                TriggerEvent = eventType,
                TriggeringEventSequence = eventSequence,
            };

            for (var i = 0; i < workflow.Actions.Count; i++)
            {
                run.Actions.Add(new WorkflowActionAttempt
                {
                    Ordinal = i,
                    ActionType = workflow.Actions[i].Type,
                    // Copied rather than referenced. A definition edited between queueing and
                    // running would otherwise change what a queued run sends, and the operator who
                    // edited it is not expecting to have rewritten yesterday's outbox.
                    Parameters = new Dictionary<string, string>(workflow.Actions[i].Parameters),
                    IdempotencyKey = $"{run.Id:N}-{i}",
                });
            }

            run.Recompute();
            _session.Store(run);
            queued++;
        }

        if (queued > 0) await _session.SaveChangesAsync(ct);

        return queued;
    }
}
