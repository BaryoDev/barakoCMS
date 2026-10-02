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
/// tenant is named on every call and has to be the tenant of that unit of work.
/// </para>
/// <para>
/// <b>Once per key.</b> A run id is started once and a wait key is parked once, per tenant, however
/// many callers stage it and however often a handler that stages it is run again. The host
/// remembers an id or a key for as long as it keeps finished work, which is the deployment's
/// configuration, and one reused after that is a new one. The message a start, a resume or a
/// timeout delivers is then handled at least once, like any other.
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
    /// <remarks>
    /// A second start of the same id does nothing and does not fail the caller's unit of work, so
    /// two callers racing to start one run, or one caller replaying its input, produce one run. The
    /// same id in another tenant is another run. Derive the id from what makes the run unique (the
    /// event that caused it, the time it was due) and not from a new random value, or nothing is
    /// deduplicated.
    /// </remarks>
    /// <param name="tenant">The slug of the tenant the run belongs to, which is the tenant of the current unit of work.</param>
    /// <param name="runId">What makes this run unique within the tenant. Compared exactly.</param>
    /// <param name="message">The run's first message. Routed by its own type.</param>
    /// <param name="cancellationToken">Cancels the staging, not the run once it is committed.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="tenant"/> or <paramref name="runId"/> is blank, or <paramref name="tenant"/>
    /// is not the tenant of the current unit of work.
    /// </exception>
    Task StartAsync<TMessage>(string tenant, string runId, TMessage message, CancellationToken cancellationToken)
        where TMessage : class;

    /// <summary>
    /// Parks a run until <see cref="ResumeAsync"/> names <paramref name="waitKey"/> or
    /// <paramref name="timeoutAt"/> passes, whichever comes first.
    /// </summary>
    /// <remarks>
    /// Exactly one of the two happens. A resume that commits first delivers its own message and
    /// <paramref name="onTimeout"/> is never delivered. Otherwise <paramref name="onTimeout"/> is
    /// delivered no earlier than <paramref name="timeoutAt"/> and a later resume finds nothing to
    /// resume. Every wait has a deadline, so a run whose answer never comes is not parked forever.
    ///
    /// The wait is pending once the unit of work that staged it commits, and it holds no thread and
    /// no connection while it waits. It survives a restart and a redeploy.
    ///
    /// A key that is already waiting, or that was resumed or timed out and is still remembered,
    /// does nothing. That is what makes it safe for a handler that parks a run to be run again, and
    /// it is how a run that recurs keeps to one firing per due time: park under a key derived from
    /// the next due time, with that time as the deadline.
    /// </remarks>
    /// <param name="tenant">The slug of the tenant the run belongs to, which is the tenant of the current unit of work.</param>
    /// <param name="waitKey">What the run is waiting on, unique within the tenant. Compared exactly.</param>
    /// <param name="timeoutAt">The deadline. A time already past times out at once.</param>
    /// <param name="onTimeout">The message delivered if the deadline passes first. Routed by its own type.</param>
    /// <param name="cancellationToken">Cancels the staging, not the wait once it is committed.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="tenant"/> or <paramref name="waitKey"/> is blank, or <paramref name="tenant"/>
    /// is not the tenant of the current unit of work.
    /// </exception>
    Task WaitAsync<TMessage>(string tenant, string waitKey, DateTimeOffset timeoutAt, TMessage onTimeout, CancellationToken cancellationToken)
        where TMessage : class;

    /// <summary>
    /// Stages the resume of a parked run, delivering <paramref name="message"/> in place of its timeout.
    /// </summary>
    /// <returns>
    /// False when no wait with this key is pending in this tenant: it was never parked, its parking
    /// has not committed yet, it was already resumed, or it timed out. Nothing is staged then. True
    /// when the resume is staged.
    /// </returns>
    /// <remarks>
    /// One resume wins. Of two callers resuming the same wait, one delivers its message; the other
    /// is answered false, or, if both were answered true before either committed, its unit of work
    /// fails to commit and leaves nothing behind. The same holds against the timeout. A wait is
    /// never resumed twice and never both resumed and timed out.
    ///
    /// The key is looked up in the named tenant only, so a key from one tenant resumes nothing in
    /// another.
    /// </remarks>
    /// <param name="tenant">The slug of the tenant the wait was parked in, which is the tenant of the current unit of work.</param>
    /// <param name="waitKey">The key the run was parked under. Compared exactly.</param>
    /// <param name="message">The message the run continues with. Routed by its own type.</param>
    /// <param name="cancellationToken">Cancels the staging, not the resume once it is committed.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="tenant"/> or <paramref name="waitKey"/> is blank, or <paramref name="tenant"/>
    /// is not the tenant of the current unit of work.
    /// </exception>
    Task<bool> ResumeAsync<TMessage>(string tenant, string waitKey, TMessage message, CancellationToken cancellationToken)
        where TMessage : class;
}
