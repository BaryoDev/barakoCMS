namespace barakoCMS.Features.Workflows;

/// <summary>An event whose workflow runs were not queued, kept until <see cref="MissedTriggerSweep"/> replays it.</summary>
/// <remarks>
/// Keyed by event type, entry and sequence, so a trigger that fails twice is one row. The sequence
/// is also what makes a replay safe: <see cref="WorkflowRunQueue"/> skips a workflow that already
/// has a run for the entry at that sequence. No workflow id is kept, because the enqueue that
/// failed is the one that finds the workflows, so which of them were due is not known.
/// </remarks>
internal sealed class MissedWorkflowTrigger
{
    public string Id { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public Guid ContentId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public DateTime RecordedAt { get; set; }

    /// <summary>How many replays have failed. A row at <see cref="MissedTriggerSweep.MaxAttempts"/> is left for an operator.</summary>
    public int Attempts { get; set; }

    /// <summary>The type name of the last exception, never its message, which can carry a connection string.</summary>
    public string? LastError { get; set; }

    internal static string IdFor(string eventType, Guid contentId, long sequence) =>
        $"{eventType}:{contentId:N}:{sequence}";

    internal static MissedWorkflowTrigger For(
        string eventType, Guid contentId, string tenantId, long sequence, Exception cause) => new()
    {
        Id = IdFor(eventType, contentId, sequence),
        EventType = eventType,
        ContentId = contentId,
        TenantId = tenantId,
        Sequence = sequence,
        RecordedAt = DateTime.UtcNow,
        LastError = cause.GetType().Name,
    };
}
