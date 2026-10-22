using Marten;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.Scheduling;

/// <summary>
/// Hands due entries to their handlers. Runs on every instance; the claim decides who fires.
/// </summary>
/// <remarks>
/// The claim flips Pending to Firing with one conditional UPDATE that wins only when exactly one row
/// changes. An instance that loses it changes no row and skips the entry. Only Pending entries are
/// claimed, so an entry fires at most once: a handler that crashes leaves it Failed, and an instance
/// that dies mid-handler leaves it Firing, neither is picked up again.
/// </remarks>
public sealed class ScheduleSweepService(
    IDocumentStore store,
    IEnumerable<IScheduledActionHandler> handlers,
    TimeProvider time,
    ILogger<ScheduleSweepService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Schedule sweep failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Fires every due Pending entry this instance manages to claim. Returns how many.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var fired = 0;

        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<ScheduledEntry> due;
            await using (var query = store.QuerySession())
            {
                due = await query.Query<ScheduledEntry>()
                    .Where(e => e.Status == ScheduledEntryStatus.Pending && e.DueAt <= now)
                    .OrderBy(e => e.DueAt)
                    .Take(BatchSize)
                    .ToListAsync(ct);
            }

            if (due.Count == 0)
                break;

            var claimedAny = false;
            foreach (var entry in due)
            {
                if (!await ClaimAsync(entry.Id, ct))
                    continue;

                claimedAny = true;
                if (await FireAsync(entry, ct))
                    fired++;
            }

            if (!claimedAny)
                break;
        }

        return fired;
    }

    /// <summary>
    /// Moves one Pending row to Firing in a single conditional UPDATE, so Postgres decides the winner.
    /// Two instances that both read the row as Pending cannot both match the WHERE clause. It runs
    /// on its own autocommit connection: a Marten session's connection is transactional, and the
    /// claim would roll back when the unsaved session is disposed.
    /// </summary>
    private async Task<bool> ClaimAsync(Guid id, CancellationToken ct)
    {
        var table = $"{store.Options.DatabaseSchemaName}.mt_doc_scheduled_entries";
        await using var connection = store.Storage.Database.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE {table} SET data = jsonb_set(data, '{{Status}}', '{(int)ScheduledEntryStatus.Firing}'::jsonb), " +
            "mt_version = mt_version + 1, mt_last_modified = transaction_timestamp() " +
            $"WHERE id = @id AND data ->> 'Status' = '{(int)ScheduledEntryStatus.Pending}'";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private async Task<bool> FireAsync(ScheduledEntry entry, CancellationToken ct)
    {
        var handler = handlers.FirstOrDefault(h => string.Equals(h.Action, entry.Action, StringComparison.Ordinal));
        string? error = null;

        if (handler is null)
        {
            error = $"No handler is registered for action '{entry.Action}'.";
        }
        else
        {
            try
            {
                await handler.HandleAsync(
                    new ScheduledActionContext(entry.Id, entry.TenantId, entry.Action, entry.DueAt, entry.Payload), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduled action {Action} failed for entry {Id}", entry.Action, entry.Id);
                error = ex.Message;
            }
        }

        await using var session = store.LightweightSession();
        var current = await session.LoadAsync<ScheduledEntry>(entry.Id, ct);
        if (current is null)
            return false;

        current.Status = error is null ? ScheduledEntryStatus.Fired : ScheduledEntryStatus.Failed;
        current.FiredAt = time.GetUtcNow();
        current.Error = error;
        session.Store(current);
        await session.SaveChangesAsync(ct);
        return error is null;
    }
}
