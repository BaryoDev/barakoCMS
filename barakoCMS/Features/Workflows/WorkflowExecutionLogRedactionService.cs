using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Features.Workflows;

/// <summary>
/// Redacts, in place, workflow execution logs stored before logs were redacted when written (#608).
/// </summary>
/// <remarks>
/// <see cref="WorkflowDebugger"/> redacts an old log each time it is served, which kept the raw
/// exception messages and parameter values out of responses but left them in the table, where a
/// database dump or a backup still has them, and nothing ever removes these rows. This rewrites each
/// one through the same redaction and marks it <see cref="WorkflowExecutionLog.Redacted"/>.
///
/// It runs once at startup, after the schema is applied (hosted services start after
/// <c>ApplyMartenSchemaAsync</c>), over every partition holding an unredacted log. It is safe on
/// every boot and on several instances at once: a redacted log is never selected again, and two
/// instances racing on one row write the same redacted document.
/// </remarks>
internal sealed class WorkflowExecutionLogRedactionService : BackgroundService
{
    private const int BatchSize = 200;

    /// <summary>
    /// A log stored before the flag existed has no Redacted key at all, which a LINQ comparison on
    /// the boolean would read as null and skip.
    /// </summary>
    private const string UnredactedSql = "coalesce((d.data ->> 'Redacted')::boolean, false) = false";

    private readonly IDocumentStore _store;
    private readonly ILogger<WorkflowExecutionLogRedactionService> _logger;

    public WorkflowExecutionLogRedactionService(IDocumentStore store, ILogger<WorkflowExecutionLogRedactionService> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RedactAllTenantsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Logged, not fatal. Reads still redact an old log, so a failed pass leaves responses
            // exactly as they were, and the next boot tries again.
            _logger.LogError(ex, "Could not redact stored workflow execution logs");
        }
    }

    public async Task<int> RedactAllTenantsAsync(CancellationToken ct)
    {
        await _store.Storage.Database.EnsureStorageExistsAsync(typeof(WorkflowExecutionLog), ct);

        var redacted = 0;
        foreach (var tenantId in await PartitionsAsync(ct))
        {
            await using var session = _store.LightweightSession(tenantId);
            redacted += await RedactStoredAsync(session, ct);
        }

        if (redacted > 0)
        {
            _logger.LogInformation("Redacted {Count} stored workflow execution log(s)", redacted);
        }

        return redacted;
    }

    private async Task<IReadOnlyList<string>> PartitionsAsync(CancellationToken ct)
    {
        var partitions = new List<string>();
        var table = _store.Options.Schema.For<WorkflowExecutionLog>();

        await using var conn = _store.Storage.Database.CreateConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select distinct tenant_id from {table} d where {UnredactedSql}";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            partitions.Add(reader.GetString(0));
        }

        return partitions;
    }

    /// <summary>Redacts every unredacted log in one partition. Pure over the session, so a test drives it directly.</summary>
    /// <returns>The number of logs that were rewritten.</returns>
    public static async Task<int> RedactStoredAsync(IDocumentSession session, CancellationToken ct)
    {
        var redacted = 0;

        while (true)
        {
            // Always the first page: every row saved below drops out of the filter, so paging with
            // Skip would step over rows instead.
            var batch = await session.Query<WorkflowExecutionLog>()
                .Where(l => l.MatchesSql(UnredactedSql))
                .OrderBy(l => l.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            var dirty = 0;
            foreach (var log in batch)
            {
                // A row whose key is missing reads Redacted false, so RedactStored always changes it.
                if (!WorkflowDebugger.RedactStored(log)) continue;

                session.Store(log);
                dirty++;
            }

            if (dirty > 0)
            {
                await session.SaveChangesAsync(ct);
                redacted += dirty;
            }

            if (batch.Count < BatchSize || dirty == 0) break;
        }

        return redacted;
    }
}
