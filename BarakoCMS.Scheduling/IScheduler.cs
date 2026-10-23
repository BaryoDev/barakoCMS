namespace BarakoCMS.Scheduling;

public interface IScheduler
{
    /// <summary>Schedules <paramref name="action"/> for <paramref name="dueAt"/> and returns the entry id.</summary>
    Task<Guid> ScheduleAsync(
        string? tenantId, string action, DateTimeOffset dueAt, string? payload = null, CancellationToken ct = default);

    /// <summary>Cancels a Pending entry. False if it does not exist or has already been claimed.</summary>
    Task<bool> CancelAsync(Guid id, CancellationToken ct = default);
}

/// <summary>What a handler receives for one due entry.</summary>
public sealed record ScheduledActionContext(
    Guid EntryId, string? TenantId, string Action, DateTimeOffset DueAt, string? Payload);

/// <summary>
/// Runs the entries whose <see cref="Action"/> matches. A handler that throws marks the entry Failed;
/// it is not retried.
/// </summary>
public interface IScheduledActionHandler
{
    string Action { get; }

    Task HandleAsync(ScheduledActionContext context, CancellationToken ct);
}
