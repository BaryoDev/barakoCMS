namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Queues a message to be handled later, in the same transaction as the write that caused it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Staged, not sent.</b> Both members stage into the current unit of work, which is the scoped
/// session the caller is already writing with, and do not commit. The message is stored when the
/// caller saves that session, or not at all: a caller that throws before saving, or whose save
/// fails, has queued nothing. A caller that stages and never saves has also queued nothing, so save
/// afterwards. A unit of work may be saved more than once, and each save commits what was staged
/// since the one before. Resolve this from the scope that is doing the writing.
/// </para>
/// <para>
/// <b>The tenant is the unit of work's.</b> A message is stored for the tenant of the session it
/// was staged in, its handler runs in that tenant, and
/// <see cref="DurableMessageContext.Tenant"/> tells the handler which. No member takes a tenant, so
/// a message cannot be queued for one tenant from a unit of work that belongs to another: work for
/// another tenant is staged from a unit of work opened for that tenant.
/// </para>
/// <para>
/// <b>Attempted at least once.</b> A message that was committed is given to the
/// <see cref="IDurableMessageHandler{TMessage}"/> registered for its type at least once, and ends
/// either handled or as a dead letter. It survives a restart and a redeploy. It can be given to its
/// handler more than once, and no order is promised between two messages, including two staged in
/// one unit of work. A caller that needs one message per key starts a run through
/// <see cref="IDurableRuns"/>.
/// </para>
/// <para>
/// <b>The message is stored data.</b> It is written as JSON and read back before it is handled, so
/// the handler never receives the instance that was queued. Use a record or a class with public
/// properties, and nothing that does not survive that round trip. It is stored under its own type's
/// name, so renaming or moving the type, or changing its shape incompatibly, strands the messages
/// already stored. It must not carry a credential.
/// </para>
/// <para>
/// <b>The host implements this.</b> A module calls it and registers handlers. Retry policy, queues
/// and which node does the work are the host's configuration and are not reachable from here.
/// </para>
/// </remarks>
public interface IDurableOutbox
{
    /// <summary>
    /// Stages a message to be handled as soon as the current unit of work commits.
    /// </summary>
    /// <returns>
    /// The message's id, assigned here and not at the commit. It is the
    /// <see cref="DurableMessageContext.MessageId"/> the handler is given on every delivery, so the
    /// caller can store it beside its own write. It names nothing if the unit of work never commits.
    /// </returns>
    /// <param name="message">The message. Routed by its own type, not by <typeparamref name="TMessage"/>.</param>
    /// <param name="cancellationToken">Cancels the staging, not the message once it is committed.</param>
    Task<string> EnqueueAsync<TMessage>(TMessage message, CancellationToken cancellationToken)
        where TMessage : class;

    /// <summary>
    /// Stages a message to be handled no earlier than <paramref name="dueAt"/>.
    /// </summary>
    /// <remarks>
    /// The time is a floor. How soon after it the message is handled depends on how often the host
    /// looks for due work, and nothing here promises an upper bound. A time already past is due at
    /// once. The wait is stored with the message, so it survives a restart and a redeploy.
    /// </remarks>
    /// <returns>The message's id, as <see cref="EnqueueAsync"/> returns it.</returns>
    /// <param name="message">The message. Routed by its own type, not by <typeparamref name="TMessage"/>.</param>
    /// <param name="dueAt">The earliest moment the message may be handled.</param>
    /// <param name="cancellationToken">Cancels the staging, not the message once it is committed.</param>
    Task<string> ScheduleAsync<TMessage>(TMessage message, DateTimeOffset dueAt, CancellationToken cancellationToken)
        where TMessage : class;
}
