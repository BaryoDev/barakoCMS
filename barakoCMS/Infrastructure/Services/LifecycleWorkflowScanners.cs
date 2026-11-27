using barakoCMS.Core;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>Finds workflows that listen for a transition the edit removes or changes.</summary>
internal sealed class LifecycleWorkflowTriggerScanner(LifecycleWorkflows workflows) : ILifecycleScanner
{
    public async Task<LifecycleScan> ScanAsync(IQuerySession session, LifecycleChange change, CancellationToken ct)
    {
        var blockers = new List<LifecycleBlocker>();
        foreach (var workflow in (await workflows.AllAsync(session, ct)).OrderBy(w => w.Name, StringComparer.Ordinal))
        {
            foreach (var name in LifecycleReferences.TriggeredBy(workflow, change.Type).Distinct(StringComparer.Ordinal))
            {
                if (LifecycleReferences.Changes(change, name, exact: true) is { } changed)
                {
                    blockers.Add(new LifecycleBlocker(
                        LifecycleBlockerKind.WorkflowTrigger, null, changed.Name, workflow.Name, null,
                        $"The workflow '{workflow.Name}' runs on the '{changed.Name}' transition, which this edit removes or changes."));
                }
            }
        }

        return LifecycleScan.Blocking(blockers);
    }
}

/// <summary>Finds Transition actions, nested and compensating ones included, that make a transition the edit removes or changes.</summary>
internal sealed class LifecycleWorkflowActionScanner(LifecycleWorkflows workflows) : ILifecycleScanner
{
    public async Task<LifecycleScan> ScanAsync(IQuerySession session, LifecycleChange change, CancellationToken ct)
    {
        var blockers = new List<LifecycleBlocker>();
        foreach (var workflow in (await workflows.AllAsync(session, ct)).OrderBy(w => w.Name, StringComparer.Ordinal))
        {
            var names = WorkflowSaveRights.Flatten(workflow)
                .Where(a => string.Equals(a.Type, "Transition", StringComparison.OrdinalIgnoreCase)
                    && ContentTypeName.Same(a.Parameters.GetValueOrDefault("ContentType"), change.Type.Name))
                .Select(a => a.Parameters.GetValueOrDefault("Transition")?.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (LifecycleReferences.Changes(change, name) is { } changed)
                {
                    blockers.Add(new LifecycleBlocker(
                        LifecycleBlockerKind.WorkflowAction, null, changed.Name, workflow.Name, null,
                        $"The workflow '{workflow.Name}' makes the '{changed.Name}' transition, which this edit removes or changes."));
                }
            }
        }

        return LifecycleScan.Blocking(blockers);
    }
}

/// <summary>Finds runs still waiting or running, or still undoing, that carry a transition the edit removes or changes.</summary>
internal sealed class LifecycleWorkflowRunScanner : ILifecycleScanner
{
    public async Task<LifecycleScan> ScanAsync(IQuerySession session, LifecycleChange change, CancellationToken ct)
    {
        if (change.ChangedTransitions.Count == 0)
        {
            return LifecycleScan.None;
        }

        var open = await session.Query<WorkflowRun>()
            .Where(r => r.Status == RunStatus.Pending || r.Status == RunStatus.Running
                || r.Compensation == RunCompensation.Compensating)
            .ToListAsync(ct);
        var blockers = new List<LifecycleBlocker>();
        foreach (var group in open.Where(r => ContentTypeName.Same(r.ContentType, change.Type.Name))
                     .SelectMany(run => TransitionsOf(run).Select(name => (run, name)))
                     .Where(x => LifecycleReferences.Changes(change, x.name) is not null)
                     .GroupBy(x => (x.run.WorkflowName, Name: LifecycleReferences.Changes(change, x.name)!.Name),
                         x => x.run.Id)
                     .OrderBy(g => g.Key.WorkflowName, StringComparer.Ordinal))
        {
            var count = group.Distinct().Count();
            blockers.Add(new LifecycleBlocker(
                LifecycleBlockerKind.WorkflowRun, null, group.Key.Name, group.Key.WorkflowName, count,
                $"{count} run(s) of the workflow '{group.Key.WorkflowName}' have not finished and carry the '{group.Key.Name}' transition, "
                + "which this edit removes or changes. Let them finish or cancel them first."));
        }

        return LifecycleScan.Blocking(blockers);
    }

    private static IEnumerable<string> TransitionsOf(WorkflowRun run)
    {
        if (WorkflowEvents.IsTransition(run.TriggerEvent) && WorkflowEvents.TransitionName(run.TriggerEvent) is { } fired)
        {
            yield return fired;
        }

        foreach (var attempt in run.Actions.Where(a => string.Equals(a.ActionType, "Transition", StringComparison.OrdinalIgnoreCase)
                     && a.Status is AttemptStatus.Pending or AttemptStatus.Running))
        {
            if (attempt.Parameters.GetValueOrDefault("Transition")?.Trim() is { Length: > 0 } named)
            {
                yield return named;
            }
        }

        foreach (var attempt in run.Actions.Where(a => a.Compensation?.Status is CompensationStatus.Pending or CompensationStatus.Running
                     && string.Equals(a.Compensate?.Type, "Transition", StringComparison.OrdinalIgnoreCase)))
        {
            if (attempt.Compensate!.Parameters.GetValueOrDefault("Transition")?.Trim() is { Length: > 0 } undone)
            {
                yield return undone;
            }
        }
    }
}

/// <summary>Finds date schedules that sweep entries in a state the edit removes or renames.</summary>
internal sealed class LifecycleScheduleScanner(LifecycleWorkflows workflows) : ILifecycleScanner
{
    public async Task<LifecycleScan> ScanAsync(IQuerySession session, LifecycleChange change, CancellationToken ct)
    {
        var blockers = (await workflows.AllAsync(session, ct))
            .Where(w => w.DateSchedule is { } s && ContentTypeName.Same(s.ContentType, change.Type.Name)
                && LifecycleReferences.Strands(change, s.InState, exact: true))
            .OrderBy(w => w.Name, StringComparer.Ordinal)
            .Select(w => new LifecycleBlocker(
                LifecycleBlockerKind.Schedule, w.DateSchedule!.InState, null, w.Name, null,
                $"The schedule of the workflow '{w.Name}' sweeps entries in the state '{w.DateSchedule.InState}', which this edit removes or renames."))
            .ToList();
        return LifecycleScan.Blocking(blockers);
    }
}

/// <summary>Warns about email links that name a transition the edit removes or changes.</summary>
/// <remarks>A link in a template is read when the email is made, and a missing one is left out, so it is a warning and never a refusal.</remarks>
internal sealed class LifecycleTemplateLinkScanner(LifecycleWorkflows workflows) : ILifecycleScanner
{
    public async Task<LifecycleScan> ScanAsync(IQuerySession session, LifecycleChange change, CancellationToken ct)
    {
        var warnings = new List<LifecycleBlocker>();
        foreach (var workflow in (await workflows.AllAsync(session, ct)).OrderBy(w => w.Name, StringComparer.Ordinal))
        {
            if (!WorkflowTriggers.ContentTypes(workflow).Any(t => ContentTypeName.Same(t, change.Type.Name)))
            {
                continue;
            }

            IReadOnlyList<string> names;
            try
            {
                names = WorkflowSaveRights.Flatten(workflow)
                    .SelectMany(a => a.Parameters.Values)
                    .SelectMany(TemplateExpression.LinkedTransitions)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (TemplateScanException)
            {
                warnings.Add(new LifecycleBlocker(
                    LifecycleBlockerKind.TemplateLink, null, null, workflow.Name, null,
                    $"A template of the workflow '{workflow.Name}' could not be read in full, so the transitions it links to were not checked."));
                continue;
            }

            foreach (var name in names)
            {
                if (LifecycleReferences.Changes(change, name) is { } changed)
                {
                    warnings.Add(new LifecycleBlocker(
                        LifecycleBlockerKind.TemplateLink, null, changed.Name, workflow.Name, null,
                        $"A template of the workflow '{workflow.Name}' links to the '{changed.Name}' transition, which this edit removes or changes. "
                        + "The link will be left out of the email."));
                }
            }
        }

        return new LifecycleScan([], warnings);
    }
}
