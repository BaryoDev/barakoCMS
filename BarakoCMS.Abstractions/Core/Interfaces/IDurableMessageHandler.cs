namespace barakoCMS.Core.Interfaces;

/// <summary>
/// What a handler is told about the message it was given, beyond the message itself.
/// </summary>
public sealed class DurableMessageContext
{
    /// <summary>
    /// The slug of the tenant the message was queued for, as the caller named it.
    /// </summary>
    /// <remarks>
    /// The handler is built from a scope bound to this tenant, so a session resolved in it reads and
    /// writes this tenant's data. It is given here as well so a handler that passes the tenant on,
    /// to <see cref="IDurableOutbox"/> or to <see cref="IEmailService.SendForTenantAsync"/>, names
    /// it and does not have to find it.
    /// </remarks>
    public required string Tenant { get; init; }

    /// <summary>
    /// The message's id, the same on every delivery of it.
    /// </summary>
    /// <remarks>
    /// A message can be handled more than once, and this is what a handler deduplicates on. Send it
    /// as the idempotency key where a provider takes one, or record it with the handler's own write
    /// and check for it first. Unique across tenants.
    /// </remarks>
    public required string MessageId { get; init; }
}

/// <summary>
/// Handles one type of message queued through <see cref="IDurableOutbox"/> or <see cref="IDurableRuns"/>.
/// </summary>
/// <remarks>
/// <para>
/// A plain class. Register it the way a workflow action or a lifecycle hook is registered
/// (<c>services.AddScoped&lt;IDurableMessageHandler&lt;MyMessage&gt;, MyHandler&gt;()</c>), one
/// handler per message type. It is resolved for each delivery from a scope bound to the message's
/// tenant, so it may take scoped services, and it has no request: nothing from
/// <c>HttpContext</c> is available.
/// </para>
/// <para>
/// <b>At least once.</b> A committed message is handed to its handler until one delivery returns
/// without throwing. It is handed over again after a throw, after the host stops or crashes while
/// the handler is running, after a redeploy, and after a handler runs past the host's time limit,
/// in which case the first delivery may still be running when the second starts. So a handler is
/// idempotent on <see cref="DurableMessageContext.MessageId"/>, and it does not assume that an
/// earlier message of the same run was handled before this one.
/// </para>
/// <para>
/// <b>Failure.</b> Returning is success, including when the handler looked and found the work
/// already done or no longer wanted. A throw is a failed attempt: the host runs the handler again
/// later, with backoff, up to the deployment's attempt budget, and then keeps the message as a dead
/// letter for an operator and does not run it again. A delivery that finds no handler registered
/// for the type, as on a node still running the previous build, is a failed attempt too. A handler
/// that knows a failure is permanent, or that wants its own retry timing, records the outcome,
/// schedules what should follow through <see cref="IDurableOutbox.ScheduleAsync"/> and returns.
/// </para>
/// <para>
/// <b>The handler owns its transaction.</b> As an endpoint does, it saves what it wrote, and what
/// it staged through <see cref="IDurableOutbox"/> or <see cref="IDurableRuns"/> commits with that
/// save or not at all. The host does not save for it.
/// </para>
/// </remarks>
/// <typeparam name="TMessage">The message type, matched exactly. A handler for a base type is not given a derived one.</typeparam>
public interface IDurableMessageHandler<TMessage>
    where TMessage : class
{
    /// <summary>
    /// Does the work the message asks for.
    /// </summary>
    /// <param name="message">The message, read back from what was stored. Never the instance that was queued.</param>
    /// <param name="context">The tenant and the message's id.</param>
    /// <param name="cancellationToken">
    /// Cancelled when the host is stopping and when the handler runs past the host's time limit. A
    /// handler that stops for it has not handled the message, and the message is delivered again.
    /// </param>
    Task HandleAsync(TMessage message, DurableMessageContext context, CancellationToken cancellationToken);
}
