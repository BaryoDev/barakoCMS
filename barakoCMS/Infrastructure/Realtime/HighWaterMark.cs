using Marten;

namespace barakoCMS.Infrastructure.Realtime;

/// <summary>
/// The highest event sequence below which every transaction has finished. Readers that stop here
/// never pass an event that is still committing.
/// </summary>
public interface IHighWaterMark
{
    Task<long> CurrentAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads the mark the async daemon's high-water agent keeps in <c>mt_event_progression</c>. One
/// mark for the whole store: sequences are global across tenants, so one ceiling serves them all.
/// </summary>
internal sealed class MartenHighWaterMark(IDocumentStore store) : IHighWaterMark
{
    public async Task<long> CurrentAsync(CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var schema = store.Options.Events.DatabaseSchemaName;
        var rows = await session.QueryAsync<long>(
            $"select coalesce(max(last_seq_id), 0) from {schema}.mt_event_progression where name = ?",
            ct, "HighWaterMark");
        return rows.SingleOrDefault();
    }
}
