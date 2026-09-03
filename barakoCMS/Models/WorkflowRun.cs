namespace barakoCMS.Models;

/// <summary>
/// One firing of a workflow: what was decided, what has been attempted, and how each went.
/// </summary>
/// <remarks>
/// This is a work queue as much as a record. The projection writes it and returns; a background
/// runner picks the attempts up and executes them.
///
/// The split exists because <c>WorkflowProjection</c> runs inside Marten's async daemon, which
/// processes a shard sequentially. An action that posts to Facebook, then emails a list, then
/// tweets holds that shard for three third-party calls: a slow provider stalls workflow processing
/// for every tenant, and a hanging one stops it. That is also why the engine swallowed every
/// exception, and why nothing until now knew whether an action had worked.
///
/// It is the outbox pattern, and the event stream was already half of it.
/// </remarks>
public class WorkflowRun
{
    public Guid Id { get; set; }

    public Guid WorkflowDefinitionId { get; set; }

    /// <summary>The workflow's name when it fired, so a run stays readable after a rename.</summary>
    public string WorkflowName { get; set; } = string.Empty;

    public Guid ContentId { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public string TriggerEvent { get; set; } = string.Empty;

    /// <summary>
    /// The sequence of the event that caused this run.
    /// </summary>
    /// <remarks>
    /// Carried so a retry can tell whether it has been overtaken. A run for revision 4 that is
    /// retried after revision 7 was published posts stale content, and the operator pressing retry
    /// has no way to know that from the error message alone.
    /// </remarks>
    public long TriggeringEventSequence { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    public List<WorkflowActionAttempt> Actions { get; set; } = new();

    /// <summary>Recomputes <see cref="Status"/> from the attempts.</summary>
    /// <remarks>
    /// PartiallyFailed is a real state rather than a rounding of Failed. "Post to Facebook, then
    /// email, then tweet" is three independent things, and reporting the whole run as failed because
    /// the mail server was down hides that two of them went out, which is exactly what an operator
    /// deciding whether to retry needs to know.
    /// </remarks>
    public void Recompute()
    {
        if (Actions.Count == 0)
        {
            Status = RunStatus.Succeeded;
            CompletedAt ??= DateTimeOffset.UtcNow;
            return;
        }

        if (Actions.Any(a => a.Status is AttemptStatus.Pending or AttemptStatus.Running))
        {
            Status = Actions.Any(a => a.Status != AttemptStatus.Pending) ? RunStatus.Running : RunStatus.Pending;
            return;
        }

        var succeeded = Actions.Count(a => a.Status is AttemptStatus.Succeeded or AttemptStatus.Skipped);

        Status = succeeded == Actions.Count
            ? RunStatus.Succeeded
            : succeeded == 0 ? RunStatus.Failed : RunStatus.PartiallyFailed;

        CompletedAt ??= DateTimeOffset.UtcNow;
    }
}

public enum RunStatus { Pending, Running, Succeeded, Failed, PartiallyFailed }

/// <summary>One action of a run, and every attempt at it collapsed into its current state.</summary>
public class WorkflowActionAttempt
{
    /// <summary>Position in the run. Actions execute in this order.</summary>
    public int Ordinal { get; set; }

    public string ActionType { get; set; } = string.Empty;

    public Dictionary<string, string> Parameters { get; set; } = new();

    public AttemptStatus Status { get; set; } = AttemptStatus.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>
    /// Stable across retries of the same action, derived from the run id and the ordinal.
    /// </summary>
    /// <remarks>
    /// Sent as a header where a provider supports one. A retry without this posts twice, and the
    /// case it matters most for is the one nobody tests: a timeout, where the request may well have
    /// arrived.
    /// </remarks>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Which node holds this attempt, and until when.</summary>
    /// <remarks>
    /// A lease rather than a lock. The scheduler's advisory lock serialises everything, which is the
    /// wrong shape here: two nodes should work in parallel on different attempts. A node that dies
    /// mid-attempt releases its work when the lease expires, without anything having to notice it
    /// died.
    /// </remarks>
    public string? LeasedBy { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public int? ResponseStatus { get; set; }

    /// <summary>Why it failed, truncated. Never a response body.</summary>
    /// <remarks>
    /// A 401 from an OAuth provider frequently contains the credential that was sent, and this is
    /// stored, served over the API and shown in the admin.
    /// </remarks>
    public string? Error { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public long? DurationMs { get; set; }
}

/// <summary>
/// Where one action stands.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the one worth explaining. A timeout is not a failure: the request may
/// have arrived and the response may have been lost. Retrying it automatically is how a customer
/// gets two invoices. It is a distinct state, it is never retried on its own, and an operator can
/// retry it by hand having decided that duplicate delivery is the lesser risk.
/// </remarks>
public enum AttemptStatus { Pending, Running, Succeeded, Failed, Unknown, Skipped }
