using barakoCMS.Models;

namespace barakoCMS.Features.Workflows.Outbox;

/// <summary>How one send of an outbox message went.</summary>
/// <param name="Delivered">The receiver accepted it.</param>
/// <param name="Permanent">A retry cannot change the answer, so the message is dead-lettered.</param>
/// <param name="Status">The HTTP status, when one came back.</param>
/// <param name="Error">Why it was not delivered. Never a response body.</param>
internal readonly record struct OutboxSendResult(bool Delivered, bool Permanent, int? Status, string? Error, long DurationMs)
{
    public static OutboxSendResult Ok(int status, long ms) => new(true, false, status, null, ms);

    public static OutboxSendResult Retry(string error, int? status = null, long ms = 0) => new(false, false, status, error, ms);

    public static OutboxSendResult Refused(string error) => new(false, true, null, error, 0);
}

/// <summary>Sends an outbox message to the service it is addressed to.</summary>
/// <remarks>
/// One implementation ships, <see cref="SignedHttpOutboxTransport"/>. A queue adapter implements this
/// and is registered in its place.
/// </remarks>
internal interface IOutboxTransport
{
    Task<OutboxSendResult> SendAsync(OutboxMessage message, CancellationToken ct);
}
