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

    /// <summary>
    /// The earliest moment the runner could claim an attempt of this run. <see cref="DueAtOnce"/>
    /// when one can be claimed now. Null once nothing is left to claim, and on a run stored before
    /// this was kept.
    /// </summary>
    /// <remarks>
    /// On the run rather than only on its attempts so the runner can ask the database for due runs.
    /// Filtering after the read meant twenty backing-off runs hid every run queued behind them. The
    /// runner reads a null on an unfinished run as due, so a run stored without it is still claimed.
    /// </remarks>
    public DateTimeOffset? NextDueAt { get; set; }

    /// <summary>When this run was told to stop. Null for a run nobody stopped.</summary>
    /// <remarks>
    /// Kept apart from the status because a cancel leaves an attempt that is running under a live
    /// lease alone: the request is already with the third party. The run stays Running until that
    /// attempt records its outcome, and this is what tells the runner to start nothing after it and
    /// not to queue it again if it failed.
    /// </remarks>
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>What <see cref="NextDueAt"/> holds for a run with an attempt that has no wait.</summary>
    /// <remarks>
    /// A fixed moment in the past, not the writing node's clock. The reader compares with its own
    /// clock, so a time taken from a node that runs ahead would make every other node wait out the
    /// difference before claiming a run that is due.
    /// </remarks>
    public static readonly DateTimeOffset DueAtOnce = DateTimeOffset.UnixEpoch;

    public List<WorkflowActionAttempt> Actions { get; set; } = new();

    /// <summary>Recomputes <see cref="Status"/> and <see cref="NextDueAt"/> from the attempts.</summary>
    /// <remarks>
    /// PartiallyFailed is a real state rather than a rounding of Failed. "Post to Facebook, then
    /// email, then tweet" is three independent things, and reporting the whole run as failed because
    /// the mail server was down hides that two of them went out, which is exactly what an operator
    /// deciding whether to retry needs to know.
    ///
    /// A stopped run reads Cancelled once nothing of it is waiting or in flight, whatever its
    /// actions did, an Unknown one included. The attempts say which of them went out.
    /// </remarks>
    public void Recompute()
    {
        NextDueAt = EarliestClaim();

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

        if (CancelledAt is not null || Actions.Any(a => a.Status == AttemptStatus.Cancelled))
        {
            Status = RunStatus.Cancelled;
            CompletedAt ??= DateTimeOffset.UtcNow;
            return;
        }

        // An action skipped because the one before it failed did not go well, so it does not count
        // towards a run that succeeded. Skipped because the content went still does.
        var succeeded = Actions.Count(a =>
            a.Status == AttemptStatus.Succeeded || (a.Status == AttemptStatus.Skipped && a.HaltedBy is null));

        Status = succeeded == Actions.Count
            ? RunStatus.Succeeded
            : succeeded == 0 ? RunStatus.Failed : RunStatus.PartiallyFailed;

        CompletedAt ??= DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Stops the run: every attempt that has not started becomes Cancelled, and the run is marked so
    /// nothing more is started.
    /// </summary>
    /// <remarks>
    /// An attempt running under a live lease is left. Its request is already with the third party
    /// and changing the record would not recall it.
    ///
    /// An attempt still marked Running after its lease ran out is one the runner would start again,
    /// so it is stopped too, but as Unknown and not as Cancelled. It was claimed, so its request may
    /// have gone out, and Cancelled is kept for an action that never did.
    ///
    /// A run with nothing waiting and nothing in flight has finished, and is left exactly as it is.
    /// Marking it would rewrite what a run that succeeded or failed says happened.
    /// </remarks>
    /// <returns>How many attempts were stopped, Cancelled and Unknown together.</returns>
    public int Cancel(DateTimeOffset now)
    {
        if (!Actions.Any(a => a.Status is AttemptStatus.Pending or AttemptStatus.Running)) return 0;

        CancelledAt ??= now;

        var stopped = 0;

        foreach (var attempt in Actions)
        {
            var abandoned = attempt.Status == AttemptStatus.Running && !(attempt.LeaseExpiresAt > now);
            if (attempt.Status != AttemptStatus.Pending && !abandoned) continue;

            if (abandoned)
            {
                attempt.Status = AttemptStatus.Unknown;
                attempt.Error = InFlightWhenStopped;
            }
            else
            {
                attempt.Status = AttemptStatus.Cancelled;
            }

            attempt.NextAttemptAt = null;
            attempt.LeasedBy = null;
            attempt.LeaseExpiresAt = null;
            attempt.CompletedAt = now;
            stopped++;
        }

        Recompute();
        return stopped;
    }

    /// <summary>
    /// Skips what is left after an attempt that ended Failed or Unknown, when that attempt is set to
    /// <see cref="WorkflowFailurePolicy.Halt"/>.
    /// </summary>
    /// <remarks>
    /// Skipped and not left Pending. A Pending attempt reads as work the runner will get to, and
    /// the run would stay unfinished with nothing in it that can be claimed.
    ///
    /// Does nothing for an attempt that is still waiting on a retry, that succeeded, that was
    /// skipped because the content went, or that is set to continue. Attempts before it, and
    /// attempts after it that already finished, are left as they are. The caller recomputes.
    /// </remarks>
    /// <returns>How many attempts were skipped.</returns>
    public int HaltAfter(WorkflowActionAttempt failed, DateTimeOffset now)
    {
        if (failed.OnFailure != WorkflowFailurePolicy.Halt) return 0;
        if (failed.Status is not (AttemptStatus.Failed or AttemptStatus.Unknown)) return 0;

        var skipped = 0;

        foreach (var attempt in Actions)
        {
            if (attempt.Ordinal <= failed.Ordinal || attempt.Status != AttemptStatus.Pending) continue;

            attempt.Status = AttemptStatus.Skipped;
            attempt.HaltedBy = failed.Ordinal;
            attempt.Error = SkippedAfterHalt;
            attempt.NextAttemptAt = null;
            attempt.CompletedAt = now;
            skipped++;
        }

        return skipped;
    }

    /// <summary>
    /// Queues again every attempt that was skipped because the attempt at this ordinal failed.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="HaltAfter"/>, for when the failed attempt is retried. Only the
    /// attempts that one skipped: an attempt skipped because the content went stays skipped. The
    /// caller recomputes.
    /// </remarks>
    /// <returns>How many attempts were queued again.</returns>
    public int ResumeAfter(int ordinal)
    {
        var resumed = 0;

        foreach (var attempt in Actions)
        {
            if (attempt.Status != AttemptStatus.Skipped || attempt.HaltedBy != ordinal) continue;

            attempt.Status = AttemptStatus.Pending;
            attempt.HaltedBy = null;
            attempt.Error = null;
            attempt.NextAttemptAt = null;
            attempt.CompletedAt = null;
            resumed++;
        }

        return resumed;
    }

    /// <summary>The error on an attempt skipped because an earlier one set to halt failed.</summary>
    public const string SkippedAfterHalt =
        "Not run: an earlier action failed and is set to halt the run. Retry that action to run this one.";

    /// <summary>The error on an attempt that was claimed, never reported back, and was then stopped.</summary>
    public const string InFlightWhenStopped =
        "The action was in flight when the run was stopped and never reported back, so it is not known whether it arrived.";

    /// <summary>
    /// Mirrors the order the runner claims in: a waiting attempt does not hold up the ones after it
    /// unless it is set to halt, and nothing past a running attempt is looked at until that one
    /// finishes or its lease runs out.
    /// </summary>
    private DateTimeOffset? EarliestClaim()
    {
        DateTimeOffset? earliest = null;

        foreach (var attempt in Actions.OrderBy(a => a.Ordinal))
        {
            if (attempt.Status == AttemptStatus.Running)
            {
                return Earlier(earliest, attempt.LeaseExpiresAt ?? DueAtOnce);
            }

            if (attempt.Status != AttemptStatus.Pending) continue;

            earliest = Earlier(earliest, attempt.NextAttemptAt ?? DueAtOnce);

            if (attempt.OnFailure == WorkflowFailurePolicy.Halt) return earliest;
        }

        return earliest;
    }

    private static DateTimeOffset Earlier(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is { } value && value < candidate ? value : candidate;
}

/// <remarks>
/// Stored as a number, so a new value goes on the end. Cancelled is a run somebody stopped, or one
/// whose workflow was switched off or deleted while it waited.
/// </remarks>
public enum RunStatus { Pending, Running, Succeeded, Failed, PartiallyFailed, Cancelled }

/// <summary>One action of a run, and every attempt at it collapsed into its current state.</summary>
public class WorkflowActionAttempt
{
    /// <summary>Position in the run. Actions execute in this order.</summary>
    public int Ordinal { get; set; }

    public string ActionType { get; set; } = string.Empty;

    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>Copied from the workflow's action when the run was queued.</summary>
    /// <remarks>An attempt stored before this existed reads as Continue.</remarks>
    public WorkflowFailurePolicy OnFailure { get; set; } = WorkflowFailurePolicy.Continue;

    /// <summary>
    /// For a Skipped attempt, the ordinal of the attempt whose failure skipped it. Null for any
    /// other status, and for an attempt skipped because the content no longer exists.
    /// </summary>
    /// <remarks>
    /// What tells the two kinds of Skipped apart. Retrying the attempt named here queues this one
    /// again, and this one cannot be retried on its own while that one has not succeeded.
    /// </remarks>
    public int? HaltedBy { get; set; }

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

    /// <summary>
    /// For a Failed attempt, whether the failure was one a retry could fix. Null for any other
    /// status, and for a failure recorded before this was kept.
    /// </summary>
    /// <remarks>
    /// False is a malformed URL, an unknown action type, or a Conditional whose earlier child already
    /// went out: the same on the fifth attempt as the first, or worse. An operator reading the run
    /// needs to tell that apart from a provider outage, and the retry endpoint records which one a
    /// person chose to retry.
    /// </remarks>
    public bool? Retryable { get; set; }

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
///
/// <see cref="Cancelled"/> is kept apart from <see cref="Skipped"/> for the same kind of reason.
/// Skipped is the content having gone, which nobody decided. Cancelled is somebody stopping the run
/// before the action started, so a Cancelled action never went out.
///
/// Skipped is also an action that did not run because an earlier one set to halt failed. That one
/// carries <see cref="WorkflowActionAttempt.HaltedBy"/>, and it never went out either.
/// </remarks>
public enum AttemptStatus { Pending, Running, Succeeded, Failed, Unknown, Skipped, Cancelled }
