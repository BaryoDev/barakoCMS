using barakoCMS.Core.Interfaces;
using JasperFx.Events;
using Marten;

namespace barakoCMS.Infrastructure.Envelope;

/// <summary>
/// Hands out this node's next origin sequence number from the gapless counter, inside the session's
/// transaction: a save that rolls back gives its numbers back.
/// </summary>
/// <remarks>
/// The series is <c>origin.{nodeId:N}</c>, the name a module may already use, so the two never
/// number the same event twice.
/// </remarks>
internal sealed class OriginSequence(INumberAllocator allocator) : IOriginSequence
{
    public async Task<long> ReserveAsync(IDocumentSession session, Guid nodeId, CancellationToken ct)
    {
        var series = new NumberSeries($"origin.{nodeId:N}", Prefix: "", Padding: 1, Reset: NumberReset.Never);
        return (await allocator.NextAsync(session, series, ct)).Value;
    }

    /// <summary>The node and a new number for an event, unless it already carries a number.</summary>
    internal async Task StampAsync(IDocumentSession session, Guid nodeId, IEvent @event, CancellationToken ct)
    {
        if (@event.GetHeader(EnvelopeHeaders.OriginSequence) is not null) return;

        @event.SetHeader(EnvelopeHeaders.OriginNodeId, nodeId.ToString());
        @event.SetHeader(EnvelopeHeaders.OriginSequence, await ReserveAsync(session, nodeId, ct));
    }

    /// <summary>The same headers for an event that is not staged yet (a batch's deferred stream).</summary>
    internal async Task<Dictionary<string, object>> HeadersAsync(IDocumentSession session, Guid nodeId, CancellationToken ct) =>
        new()
        {
            [EnvelopeHeaders.OriginNodeId] = nodeId.ToString(),
            [EnvelopeHeaders.OriginSequence] = await ReserveAsync(session, nodeId, ct),
        };
}
