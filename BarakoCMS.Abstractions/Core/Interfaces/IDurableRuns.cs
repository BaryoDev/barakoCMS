namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Starts a run once, parks it until something answers or a deadline passes, and resumes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A run is the caller's.</b> This keeps no state for a run beyond the id it was started under
/// and the keys it is waiting on. What a run has done, and what it does next, live in the caller's
/// own document, and each step is a message handled by an
/// <see cref="IDurableMessageHandler{TMessage}"/>.
/// </para>
/// <para>
/// <b>Staged, not sent.</b> Every member stages into the current unit of work, as
/// <see cref="IDurableOutbox"/> does, and takes effect when the caller saves it, or not at all. The
/// tenant is that unit of work's, and ids and keys are looked up in that tenant only.
/// </para>
/// <para>
/// <b>Once per key, and the caller is told.</b> A run id is started once and a wait key is parked
/// once, per tenant. Every member answers false, and stages nothing, when its key cannot be used:
/// the run was already started, the wait was already parked, there is nothing to resume, or the
/// same key was already staged in this unit of work. True means staged. If another unit of work
/// then commits the same key first, this one fails to commit with
/// <see cref="DurableWorkConflictException"/> and leaves nothing behind, its other writes
/// included. So a caller never commits its own write beside a start, a wait or a resume that did
/// not happen.
/// </para>
/// <para>
/// <b>How long a key is remembered.</b> At least until the message it delivers (the start, the
/// resume or the timeout) has ended, handled or dead lettered. When a handler staged the key, also
/// at least until the message that handler was given can no longer be delivered again, so a
/// handler that is run a second time is answered false. Past that it is the deployment's
/// configuration for how long finished work is kept, and a key reused after it is a new one.
/// </para>
/// <para>
/// <b>Run ids and wait keys share one namespace per tenant with every other caller.</b> Start
/// each with the name of what owns it, a module's name or a feature's
/// (<c>workflows:</c>, <c>forms:</c>), so two callers cannot collide.
/// </para>
/// <para>
/// <b>The host implements this.</b> A module calls it and does not replace it.
/// </para>
/// </remarks>
public interface IDurableRuns
{
    /// <summary>
    /// Stages the first message of a run, unless a run with this id was already started in this tenant.
    /// </summary>
    /// <returns>
    /// True when the start is staged. False when the id was already started and is still
    /// remembered, or was already staged in this unit of work; nothing is staged then.
    /// </returns>
    /// <remarks>
    /// Two callers racing to start one run, or one caller replaying its input, produce one run. The
    /// same id in another tenant is another run. Derive the id from what makes the run unique (the
    /// event that caused it, the time it was due) and not from a new random value, or nothing is
    /// deduplicated.
    ///
    /// A caller that keeps its own document for the run stores it only when this answers true. To
    /// make a document that nothing drives impossible and not merely unlikely, derive the
    /// document's id from the run id, or let the handler of the first message create it.
    /// </remarks>
    /// <param name="runId">
    /// What makes this run unique within the tenant, starting with its owner's name. Compared exactly.
    /// </param>
    /// <param name="message">The run's first message. Routed by its own type.</param>
    /// <param name="cancellationToken">Cancels the staging, not the run once it is committed.</param>
    /// <exception cref="ArgumentException"><paramref name="runId"/> is blank.</exception>
    Task<bool> StartAsync<TMessage>(string runId, TMessage message, CancellationToken cancellationToken)
        where TMessage : class;

    /// <summary>
    /// Parks a run until <see cref="ResumeAsync"/> names <paramref name="waitKey"/> or
    /// <paramref name="timeoutAt"/> passes, whichever comes first.
    /// </summary>
    /// <returns>
    /// True when the wait is staged. False when the key is already waiting, was resumed or timed
    /// out and is still remembered, or was already staged in this unit of work. Nothing is parked
    /// then and <paramref name="onTimeout"/> will not be delivered for this call.
    /// </returns>
    /// <remarks>
    /// Exactly one of the two happens to a parked wait. A resume that commits first delivers its own
    /// message and <paramref name="onTimeout"/> is never delivered. Otherwise
    /// <paramref name="onTimeout"/> is delivered no earlier than <paramref name="timeoutAt"/> and a
    /// later resume finds nothing to resume. Every parked wait has a deadline, so a run whose answer
    /// never comes is not parked forever.
    ///
    /// The wait is pending once the unit of work that staged it commits, and it holds no thread and
    /// no connection while it waits. It survives a restart and a redeploy.
    ///
    /// A key names one wait, not one subject. A run that waits for the same thing twice (an
    /// approval asked for again) uses a new key each time, the id of the message it is waiting on
    /// or a counter, or the second wait is answered false and never times out. The false is what
    /// makes it safe for a handler that parks a run to be run again.
    ///
    /// A run that recurs parks under a key derived from its next due time, with that time as the
    /// deadline, which keeps it to one firing per due time. Its handler parks the next due time and
    /// saves first, and does its work after. Done the other way round, a tick whose work keeps
    /// failing is dead lettered before it parks the next, and the recurrence stops for good.
    /// </remarks>
    /// <param name="waitKey">
    /// What this one wait is waiting on, unique within the tenant and starting with its owner's
    /// name. Compared exactly.
    /// </param>
    /// <param name="timeoutAt">The deadline. A time already past times out at once.</param>
    /// <param name="onTimeout">The message delivered if the deadline passes first. Routed by its own type.</param>
    /// <param name="cancellationToken">Cancels the staging, not the wait once it is committed.</param>
    /// <exception cref="ArgumentException"><paramref name="waitKey"/> is blank.</exception>
    Task<bool> WaitAsync<TMessage>(string waitKey, DateTimeOffset timeoutAt, TMessage onTimeout, CancellationToken cancellationToken)
        where TMessage : class;

    /// <summary>
    /// Stages the resume of a parked run, delivering <paramref name="message"/> in place of its timeout.
    /// </summary>
    /// <returns>
    /// True when the resume is staged. False when no wait with this key is pending in this tenant
    /// (it was never parked, its parking has not committed yet, it was already resumed, or it timed
    /// out) or its resume was already staged in this unit of work. Nothing is staged then.
    /// </returns>
    /// <remarks>
    /// One resume wins. Of two callers resuming the same wait, one delivers its message; the other
    /// is answered false, or, if both were answered true before either committed, its unit of work
    /// fails to commit with <see cref="DurableWorkConflictException"/>. The same holds against the
    /// timeout. A wait is never resumed twice and never both resumed and timed out.
    /// </remarks>
    /// <param name="waitKey">The key the run was parked under. Compared exactly.</param>
    /// <param name="message">The message the run continues with. Routed by its own type.</param>
    /// <param name="cancellationToken">Cancels the staging, not the resume once it is committed.</param>
    /// <exception cref="ArgumentException"><paramref name="waitKey"/> is blank.</exception>
    Task<bool> ResumeAsync<TMessage>(string waitKey, TMessage message, CancellationToken cancellationToken)
        where TMessage : class;
}
