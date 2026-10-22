namespace BarakoCMS.Scheduling;

public enum ScheduledEntryStatus
{
    Pending = 0,

    /// <summary>Claimed by one instance and handed to its handler. Never claimed again.</summary>
    Firing = 1,

    Fired = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>An action to run once, at or after <see cref="DueAt"/>.</summary>
public class ScheduledEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tenant slug the entry belongs to, or null for the default partition.</summary>
    public string? TenantId { get; set; }

    public DateTimeOffset DueAt { get; set; }

    /// <summary>Selects the <see cref="IScheduledActionHandler"/> that receives the entry.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>The handler's input, as a JSON document.</summary>
    public string? Payload { get; set; }

    public ScheduledEntryStatus Status { get; set; } = ScheduledEntryStatus.Pending;

    public DateTimeOffset? FiredAt { get; set; }

    /// <summary>Why the entry is Failed.</summary>
    public string? Error { get; set; }

    /// <summary>Marten revision. The claim is a conditional UPDATE, so a second instance changes no row and loses.</summary>
    public int Version { get; set; }
}
