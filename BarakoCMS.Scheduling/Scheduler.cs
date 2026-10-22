using Marten;

namespace BarakoCMS.Scheduling;

internal sealed class Scheduler(IDocumentStore store) : IScheduler
{
    public async Task<Guid> ScheduleAsync(
        string? tenantId, string action, DateTimeOffset dueAt, string? payload = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var entry = new ScheduledEntry
        {
            TenantId = tenantId,
            Action = action,
            DueAt = dueAt.ToUniversalTime(),
            Payload = payload,
        };

        await using var session = store.LightweightSession();
        session.Store(entry);
        await session.SaveChangesAsync(ct);
        return entry.Id;
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken ct = default)
    {
        await using var session = store.LightweightSession();
        var entry = await session.LoadAsync<ScheduledEntry>(id, ct);
        if (entry is null || entry.Status != ScheduledEntryStatus.Pending)
            return false;

        entry.Status = ScheduledEntryStatus.Cancelled;
        session.Store(entry);
        try
        {
            await session.SaveChangesAsync(ct);
            return true;
        }
        catch (JasperFx.ConcurrencyException)
        {
            return false;
        }
    }
}
