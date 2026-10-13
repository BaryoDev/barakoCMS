using System.Linq.Expressions;
using barakoCMS.Models;

namespace barakoCMS.Features.Workflows;

/// <summary>Something that drives the runs of the workflows that name it.</summary>
/// <remarks>
/// <para>
/// Routing, not replacement: a workflow names its executor in <see cref="WorkflowDefinition.Executor"/>,
/// null being the built-in <see cref="WorkflowRunner"/>, and an executor claims only the runs routed to
/// it, read with <see cref="WorkflowExecutors.DueFor"/>. Every other workflow is untouched.
/// </para>
/// <para>
/// The <see cref="WorkflowRun"/> document is the contract, because it is what
/// <c>/api/workflow-runs</c> serves and what a console reads. An executor keeps it current under the
/// invariants <see cref="WorkflowRunner"/> keeps:
/// </para>
/// <list type="bullet">
/// <item>The attempts of one run advance in <see cref="WorkflowActionAttempt.Ordinal"/> order, one at a
/// time, and an action set to Halt holds back the ones after it.</item>
/// <item>An outcome is written only by the holder of the attempt's lease, under optimistic concurrency
/// on the run, and an outcome from a node that lost its lease is dropped.</item>
/// <item>A timeout records <see cref="AttemptStatus.Unknown"/>, never a failure, and is never retried on
/// its own, because the request may have arrived.</item>
/// <item>The idempotency key of an attempt, and of its compensation, stays the same across retries.</item>
/// <item>Only outputs the action's capture names are written, and compensations run last first.</item>
/// <item>A run with <see cref="WorkflowRun.CancelledAt"/> set, or whose workflow is switched off, starts
/// nothing more.</item>
/// </list>
/// <para>
/// Internal until a second executor has been built against it. A seam designed against one caller
/// is a guess, and a public one is frozen for the rest of the major.
/// </para>
/// </remarks>
internal interface IWorkflowExecutor
{
    /// <summary>The name a workflow stores in <see cref="WorkflowDefinition.Executor"/>.</summary>
    string Name { get; }
}

/// <summary>The executor every workflow had before the choice existed: <see cref="WorkflowRunner"/>.</summary>
internal sealed class BuiltInWorkflowExecutor : IWorkflowExecutor
{
    public string Name => WorkflowExecutors.BuiltIn;
}

/// <summary>The executors this host can route to, and the queries that route.</summary>
internal sealed class WorkflowExecutors(IEnumerable<IWorkflowExecutor> executors)
{
    /// <summary>The built-in runner's name. A workflow with no executor, or this one, is run by it.</summary>
    public const string BuiltIn = "builtin";

    private readonly HashSet<string> _names = executors.Select(e => e.Name).Append(BuiltIn).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every name a workflow may store, in order.</summary>
    public IEnumerable<string> Names => _names.Order(StringComparer.Ordinal);

    /// <summary>Whether a workflow may be saved with this executor. Null and blank mean the built-in one.</summary>
    public bool IsKnown(string? name) => string.IsNullOrWhiteSpace(name) || _names.Contains(name);

    /// <summary>What a run stores: null for the built-in runner, so a run reads as it always did.</summary>
    public static string? ForRun(WorkflowDefinition workflow) =>
        string.IsNullOrWhiteSpace(workflow.Executor) || workflow.Executor == BuiltIn ? null : workflow.Executor;

    /// <summary>The runs <paramref name="executor"/> may claim at <paramref name="now"/>.</summary>
    /// <remarks>
    /// A run stored before executors existed has none and goes to the built-in runner, which is the
    /// only one that ever ran it.
    /// </remarks>
    public static Expression<Func<WorkflowRun, bool>> DueFor(string executor, DateTimeOffset now) =>
        executor == BuiltIn
            ? r => (r.Status == RunStatus.Pending || r.Status == RunStatus.Running)
                && (r.NextDueAt == null || r.NextDueAt <= now)
                && (r.Executor == null || r.Executor == BuiltIn)
            : r => (r.Status == RunStatus.Pending || r.Status == RunStatus.Running)
                && (r.NextDueAt == null || r.NextDueAt <= now)
                && r.Executor == executor;
}
