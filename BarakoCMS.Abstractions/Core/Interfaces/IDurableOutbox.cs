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
/// afterwards. Resolve this from the scope that is doing the writing, a request's own scope or a
/// scope opened for one tenant by a background service.
/// </para>
/// <para>
/// <b>The tenant is named on every call.</b> Nothing reads it from the request or from the scope.
/// It is the tenant's slug, exactly as the registry holds it, and
/// <see cref="Models.Tenant.DefaultSlug"/> for the default tenant. It has to be the tenant of the
/// unit of work the call rides, and a call naming any other throws before anything is staged. Work
/// for another tenant is staged from a unit of work opened for that tenant.
/// </para>
/// <para>
/// <b>At least once.</b> A message that was committed is handled at least once, by the
/// <see cref="IDurableMessageHandler{TMessage}"/> registered for its type. It survives a restart and
/// a redeploy. It can be handled more than once, and no order is promised between two messages,
/// including two staged in one unit of work. A caller that needs one message per key starts a run
/// through <see cref="IDurableRuns"/>.
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
    /// <param name="tenant">The slug of the tenant the handler runs in, which is the tenant of the current unit of work.</param>
    /// <param name="message">The message. Routed by its own type, not by <typeparamref name="TMessage"/>.</param>
    /// <param name="cancellationToken">Cancels the staging, not the message once it is committed.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="tenant"/> is blank or is not the tenant of the current unit of work.
    /// </exception>
    Task EnqueueAsync<TMessage>(string tenant, TMessage message, CancellationToken cancellationToken)
        where TMessage : class;

    /// <summary>
    /// Stages a message to be handled no earlier than <paramref name="dueAt"/>.
    /// </summary>
    /// <remarks>
    /// The time is a floor. How soon after it the message is handled depends on how often the host
    /// looks for due work, and nothing here promises an upper bound. A time already past is due at
    /// once. The wait is stored with the message, so it survives a restart and a redeploy.
    /// </remarks>
    /// <param name="tenant">The slug of the tenant the handler runs in, which is the tenant of the current unit of work.</param>
    /// <param name="message">The message. Routed by its own type, not by <typeparamref name="TMessage"/>.</param>
    /// <param name="dueAt">The earliest moment the message may be handled.</param>
    /// <param name="cancellationToken">Cancels the staging, not the message once it is committed.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="tenant"/> is blank or is not the tenant of the current unit of work.
    /// </exception>
    Task ScheduleAsync<TMessage>(string tenant, TMessage message, DateTimeOffset dueAt, CancellationToken cancellationToken)
        where TMessage : class;
}
