using Marten;

namespace barakoCMS.Core.Interfaces;

public enum StockMovementKind
{
    Received,
    Reserved,
    Confirmed,
    Released,
    Expired,
}

/// <summary>One line of a counter's history. The balance is the sum of <paramref name="Delta"/>.</summary>
public sealed record StockMovement(
    string Counter,
    StockMovementKind Kind,
    long Delta,
    Guid? HoldId,
    string Reference,
    DateTimeOffset At);

/// <summary>
/// The answer to a reservation. <paramref name="Balance"/> is what the counter holds after the
/// reservation when it succeeded, and what it held when it was refused.
/// </summary>
public sealed record StockReservation(bool Succeeded, Guid? HoldId, long Balance);

/// <summary>
/// A counter that can be reserved against: stock on hand, seats on a slot, places on a class.
/// </summary>
/// <remarks>
/// Every call that changes a counter runs in <paramref name="session"/>'s transaction and queues
/// nothing it cannot undo, so a reservation and the content save that depends on it commit together
/// or not at all. Dispose the session without saving and the reservation never happened.
///
/// The decrement is one statement that holds the counter's row until the transaction ends, so two
/// callers racing for the last unit take turns and the second is refused. It is not gapless: a
/// refused or rolled back reservation leaves no trace, and a hold that expires returns its unit but
/// keeps its movement, so the history shows what happened rather than a tidy sequence.
///
/// This is not <see cref="INumberAllocator"/>. A number is handed out once and never comes back; a
/// unit of stock is taken and may be returned.
/// </remarks>
public interface IStockReservations
{
    /// <summary>
    /// Adds <paramref name="quantity"/> to the counter, creating it when it does not exist.
    /// </summary>
    /// <param name="floor">
    /// The lowest balance a reservation may leave. Used only when the counter is created; an
    /// existing counter keeps its floor.
    /// </param>
    /// <returns>The balance after the receipt.</returns>
    Task<long> ReceiveAsync(IDocumentSession session, string counter, long quantity, string reference = "", long floor = 0, CancellationToken ct = default);

    /// <summary>
    /// Takes <paramref name="quantity"/> units unless that would leave the balance under the floor.
    /// A refusal is a result, not an exception. Expired holds on the counter are returned first.
    /// </summary>
    /// <param name="holdFor">
    /// How long the hold lasts before <see cref="ReleaseExpiredAsync"/> returns the units. Null holds
    /// until it is confirmed or released. At most 365 days.
    /// </param>
    Task<StockReservation> ReserveAsync(IDocumentSession session, string counter, long quantity, TimeSpan? holdFor = null, string reference = "", CancellationToken ct = default);

    /// <summary>
    /// Makes a hold final. False when the hold is unknown, already settled, or past its expiry.
    /// </summary>
    Task<bool> ConfirmAsync(IDocumentSession session, Guid holdId, CancellationToken ct = default);

    /// <summary>Returns a held quantity to its counter. False when the hold is unknown or already settled.</summary>
    Task<bool> ReleaseAsync(IDocumentSession session, Guid holdId, CancellationToken ct = default);

    /// <summary>
    /// Returns the units of every hold in this session's tenant whose expiry is at or before
    /// <paramref name="at"/> (now when null). Idempotent. Returns the number of holds expired.
    /// </summary>
    Task<int> ReleaseExpiredAsync(IDocumentSession session, DateTimeOffset? at = null, CancellationToken ct = default);

    /// <summary>
    /// The counter's balance, or null when it does not exist. A hold past its expiry counts as
    /// returned even before a sweep has run.
    /// </summary>
    Task<long?> BalanceAsync(IQuerySession session, string counter, CancellationToken ct = default);

    /// <summary>Every movement on the counter, oldest first.</summary>
    Task<IReadOnlyList<StockMovement>> MovementsAsync(IQuerySession session, string counter, CancellationToken ct = default);
}
