using barakoCMS.Models;

namespace barakoCMS.Features.Workflows.Outbox;

/// <summary>
/// Holds the messages a Publish action produced during one attempt, so the runner can store them in
/// the same save that records the attempt as succeeded.
/// </summary>
/// <remarks>
/// Scoped to the attempt. While <see cref="Holding"/> is false, which is any caller other than the
/// runner, the action stores and saves the message itself.
/// </remarks>
internal sealed class OutboxStaging
{
    public bool Holding { get; set; }

    public List<OutboxMessage> Messages { get; } = [];
}
