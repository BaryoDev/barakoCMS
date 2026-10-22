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
    /// <remarks>
    /// A definition stored or imported with <c>conditions: null</c> reads back as null, and an entry
    /// stored without data has none. Both are read as empty: a null used to throw here, which threw
    /// the whole event away, so the other workflows on it were lost along with this one.
    /// </remarks>
    internal static bool Matches(WorkflowDefinition workflow, barakoCMS.Models.Content content)
    {
        foreach (var condition in workflow.Conditions ?? [])
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
            else if (content.Data?.TryGetValue(condition.Key, out var value) == true)
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

    /// <summary>
    /// Records the Deleted runs for an entry being erased, on the caller's session and without
    /// saving, so the runs commit with the erasure or not at all.
    /// </summary>
    /// <remarks>
    /// Takes the id and the content type and no entry, so nothing of the erased data can reach a
    /// run. A workflow with conditions is not queued: its conditions read the entry's data, and
    /// that is what is being erased.
    /// </remarks>
    /// <returns>How many runs were queued.</returns>
    Task<int> QueueDeletedAsync(Guid contentId, string contentType, CancellationToken ct);

    /// <summary>
    /// Records the runs for a trigger a module fired, and commits them.
    /// </summary>
    /// <remarks>
    /// Not checked for an earlier run of the same entry, unlike <see cref="EnqueueAsync"/>. That
    /// check is for a projection rebuild replaying stored events, and a module's trigger is not a
    /// stored event, so it is never replayed. Two fires are two things that happened.
    /// </remarks>
    /// <returns>How many runs were queued.</returns>
    Task<int> FireAsync(barakoCMS.Models.Content content, string trigger, CancellationToken ct) =>
        FireAsync(content, trigger, triggerKey: null, ct);

    /// <summary>
    /// Records the runs for a trigger a module fired, and commits them, skipping a workflow that
    /// already has a run for this entry with the same <paramref name="triggerKey"/>.
    /// </summary>
    /// <remarks>
    /// A null key is never deduped: two fires are two things that happened. A key says the caller
    /// may fire the same thing again, as a retried request does, and wants it to start once.
    /// </remarks>
    /// <returns>How many runs were queued.</returns>
    Task<int> FireAsync(barakoCMS.Models.Content content, string trigger, string? triggerKey, CancellationToken ct);

    /// <summary>
    /// Records, and commits, the run of a schedule workflow for one entry and one date value, unless
    /// a run with this <paramref name="triggerKey"/> exists already.
    /// </summary>
    /// <remarks>
    /// The key is the workflow, the entry and the date value, so one value is queued once however
    /// many sweeps see it. The caller has decided the workflow is due and has matched its conditions.
    /// </remarks>
    /// <returns>True if a run was queued, false when one with that key exists.</returns>
    Task<bool> EnqueueScheduledAsync(WorkflowDefinition workflow, barakoCMS.Models.Content content, string triggerKey, CancellationToken ct);

    /// <summary>
    /// Stages one run of one chosen workflow on one entry, as an insert on the caller's session and
    /// without saving, so the run commits with whatever else the caller writes or not at all.
    /// </summary>
    /// <remarks>
    /// An insert and not a store: with a fixed <paramref name="runId"/> a second request for the
    /// same run fails when the session saves, with <c>JasperFx.DocumentAlreadyExistsException</c>,
    /// instead of replacing the run that is already there. The caller checks the workflow is on and
    /// the entry matches; this does not, since the caller is the one that tells the operator why not.
    /// </remarks>
    WorkflowRun StageManual(
        WorkflowDefinition workflow, barakoCMS.Models.Content content, Guid? runId, Guid? startedBy);

    /// <summary>Whether any workflow fires on this event for this content type.</summary>
    Task<bool> ListensAsync(string contentType, string eventType, CancellationToken ct);
}

internal sealed class WorkflowRunQueue(IDocumentSession session, ILogger<WorkflowRunQueue> logger) : IWorkflowRunQueue
{
    public async Task<int> EnqueueAsync(barakoCMS.Models.Content content, string eventType, long eventSequence, CancellationToken ct)
    {
        var workflows = WorkflowTriggers.SwitchedOn(await session.Query<WorkflowDefinition>()
            .Where(WorkflowTriggers.FiredBy(eventType))
            .ToListAsync(ct), content.ContentType);

        if (workflows.Count == 0) return 0;

        var queued = 0;

        foreach (var workflow in workflows)
        {
            if (!WorkflowConditions.Matches(workflow, content)) continue;

            // Already queued for this exact event, so a projection rebuild does not re-send
            // everything. A rebuild replays every event ever stored, and without this the first one
            // would re-fire every email and webhook this instance has ever sent. That is the failure
            // docs/operating-workflows.md calls expensive, and it stops being possible here.
            var already = await session.Query<WorkflowRun>()
                .Where(r => r.WorkflowDefinitionId == workflow.Id
                            && r.ContentId == content.Id
                            && r.TriggeringEventSequence == eventSequence)
                .AnyAsync(ct);

            if (already)
            {
                logger.LogDebug(
                    "Workflow {WorkflowId} already has a run for content {ContentId} at sequence {Sequence}",
                    workflow.Id, content.Id, eventSequence);
                continue;
            }

            session.Store(NewRun(workflow, content.Id, content.ContentType, eventType, eventSequence));
            queued++;
        }

        if (queued > 0) await session.SaveChangesAsync(ct);

        WorkflowMetrics.Default.Queued(eventType, queued);

        return queued;
    }

    public async Task<int> QueueDeletedAsync(Guid contentId, string contentType, CancellationToken ct)
    {
        var workflows = WorkflowTriggers.SwitchedOn(await session.Query<WorkflowDefinition>()
            .Where(WorkflowTriggers.FiredBy(WorkflowEvents.Deleted))
            .ToListAsync(ct), contentType);

        var queued = 0;

        foreach (var workflow in workflows)
        {
            if (workflow.Conditions is { Count: > 0 })
            {
                logger.LogInformation(
                    "Workflow {WorkflowId} has conditions, so it is not queued for erased content {ContentId}",
                    workflow.Id, contentId);
                continue;
            }

            // No check for an earlier Deleted run of this entry. An id can be erased, created again
            // and erased again (a collection sync derives its ids), and each erasure is its own
            // event. A repeat request for an entry already gone is a 404 before it gets here.
            //
            // Sequence zero. An erasure removes the stream, so there is no event to point at, and
            // no stored event has that sequence.
            session.Store(NewRun(workflow, contentId, contentType, WorkflowEvents.Deleted, eventSequence: 0));
            queued++;
        }

        return queued;
    }

    public async Task<int> FireAsync(barakoCMS.Models.Content content, string trigger, string? triggerKey, CancellationToken ct)
    {
        var workflows = WorkflowTriggers.SwitchedOn(await session.Query<WorkflowDefinition>()
            .Where(WorkflowTriggers.FiredBy(trigger))
            .ToListAsync(ct), content.ContentType);

        var queued = 0;

        foreach (var workflow in workflows)
        {
            if (!WorkflowConditions.Matches(workflow, content))
            {
                continue;
            }

            if (triggerKey is not null && await session.Query<WorkflowRun>().AnyAsync(
                    r => r.WorkflowDefinitionId == workflow.Id && r.ContentId == content.Id && r.TriggerKey == triggerKey, ct))
            {
                continue;
            }

            // Sequence zero, as for an erasure: no stored event caused this run.
            var run = NewRun(workflow, content.Id, content.ContentType, trigger, eventSequence: 0);
            run.TriggerKey = triggerKey;
            session.Store(run);
            queued++;
        }

        if (queued > 0)
        {
            await session.SaveChangesAsync(ct);
        }

        WorkflowMetrics.Default.Queued(trigger, queued);

        return queued;
    }

    public WorkflowRun StageManual(
        WorkflowDefinition workflow, barakoCMS.Models.Content content, Guid? runId, Guid? startedBy)
    {
        // Sequence zero, as for a module's trigger: no stored event caused this run, and a run with
        // no sequence to compare is never read as overtaken by a later revision of the entry.
        var run = NewRun(workflow, content.Id, content.ContentType, WorkflowEvents.Manual, eventSequence: 0, runId);
        run.StartedBy = startedBy;
        session.Insert(run);
        return run;
    }

    public async Task<bool> EnqueueScheduledAsync(
        WorkflowDefinition workflow, barakoCMS.Models.Content content, string triggerKey, CancellationToken ct)
    {
        if (await session.Query<WorkflowRun>().AnyAsync(
                r => r.WorkflowDefinitionId == workflow.Id && r.ContentId == content.Id && r.TriggerKey == triggerKey, ct))
        {
            return false;
        }

        var trigger = ScheduleOffset.EventOf(workflow.Name);
        // The run's id comes from the key, and it is inserted. Two sweeps that both passed the check
        // above then collide on the id, and the one that lost reports the value as already queued.
        var run = NewRun(workflow, content.Id, content.ContentType, trigger, eventSequence: 0,
            runId: barakoCMS.Features.Workflows.Actions.DeterministicId.From("scheduled-run:" + triggerKey));
        run.TriggerKey = triggerKey;
        session.Insert(run);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is JasperFx.DocumentAlreadyExistsException
            || ex.InnerException is JasperFx.DocumentAlreadyExistsException)
        {
            session.EjectAllPendingChanges();
            return false;
        }

        WorkflowMetrics.Default.Queued(trigger, 1);
        return true;
    }

    public async Task<bool> ListensAsync(string contentType, string eventType, CancellationToken ct) =>
        WorkflowTriggers.SwitchedOn(await session.Query<WorkflowDefinition>()
            .Where(WorkflowTriggers.FiredBy(eventType))
            .ToListAsync(ct), contentType).Count > 0;

    private static WorkflowRun NewRun(
        WorkflowDefinition workflow, Guid contentId, string contentType, string eventType, long eventSequence,
        Guid? runId = null)
    {
        var run = new WorkflowRun
        {
            Id = runId ?? Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            WorkflowName = workflow.Name,
            ContentId = contentId,
            ContentType = contentType,
            TriggerEvent = eventType,
            Executor = WorkflowExecutors.ForRun(workflow),
            TriggeringEventSequence = eventSequence,
            // From the triggering event in the projection, and from the request for an erasure.
            CorrelationId = barakoCMS.Infrastructure.Tracing.Correlation.Id,
            TraceParent = barakoCMS.Infrastructure.Tracing.Correlation.Cause,
        };

        // A document stored or imported with no actions list, or an action with no parameters, is
        // read as none, as null conditions are: throwing here would lose the run of every other
        // workflow on the same event.
        var actions = workflow.Actions ?? [];
        for (var i = 0; i < actions.Count; i++)
        {
            run.Actions.Add(new WorkflowActionAttempt
            {
                Ordinal = i,
                ActionType = actions[i].Type,
                // Copied rather than referenced. A definition edited between queueing and
                // running would otherwise change what a queued run sends, and the operator who
                // edited it is not expecting to have rewritten yesterday's outbox.
                Parameters = new Dictionary<string, string>(actions[i].Parameters ?? []),
                OnFailure = actions[i].OnFailure ?? WorkflowFailurePolicy.Continue,
                Capture = actions[i].Capture is { } capture ? [.. capture] : [],
                Compensate = actions[i].Compensate is { } undo
                    ? new WorkflowCompensation { Type = undo.Type, Parameters = new Dictionary<string, string>(undo.Parameters ?? []) }
                    : null,
                IdempotencyKey = $"{run.Id:N}-{i}",
            });
        }

        run.Recompute();
        return run;
    }
}
