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
/// <c>ApplyMartenSchemaAsync</c>), over every tenant partition. It is safe on
/// every boot and on several instances at once: a redacted log is never selected again, and two
/// instances racing on one row would write the same redacted document. An advisory lock keeps a
/// second instance from repeating the work while the first is still going.
/// </remarks>
internal sealed class WorkflowExecutionLogRedactionService : BackgroundService
{
    private const int BatchSize = 200;

    /// <summary>A Postgres advisory lock key, arbitrary but fixed, and distinct from the retention sweeps'.</summary>
    private const long PassLockKey = 8_242_026_608L;

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

    /// <returns>The number of logs rewritten, or null when another instance held the lock.</returns>
    public async Task<int?> RedactAllTenantsAsync(CancellationToken ct)
    {
        await _store.Storage.Database.EnsureStorageExistsAsync(typeof(WorkflowExecutionLog), ct);

        await using var lockConnection = _store.Storage.Database.CreateConnection();
        await lockConnection.OpenAsync(ct);

        await using (var acquire = lockConnection.CreateCommand())
        {
            acquire.CommandText = "select pg_try_advisory_lock(@key)";
            acquire.Parameters.AddWithValue("key", PassLockKey);

            if ((bool?)await acquire.ExecuteScalarAsync(ct) is not true)
            {
                _logger.LogDebug("Another instance is redacting stored workflow execution logs; skipping.");
                return null;
            }
        }

        try
        {
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
        finally
        {
            await using var release = lockConnection.CreateCommand();
            release.CommandText = "select pg_advisory_unlock(@key)";
            release.Parameters.AddWithValue("key", PassLockKey);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    /// <summary>Every partition a log can be in: each tenant in the registry, and both names the default partition goes by.</summary>
    /// <remarks>
    /// From the registry and not a <c>select distinct tenant_id</c> over the log table. With
    /// database tenancy enforced, row level security filters that query on <c>app.tenant_id</c>,
    /// which a bare connection does not set, so it errors or sees one tenant (#877). Each partition
    /// is then queried inside its own session, which does set it. A tenant with no unredacted logs
    /// costs one query that returns nothing.
    /// </remarks>
    private async Task<IReadOnlyList<string>> PartitionsAsync(CancellationToken ct)
    {
        await using var session = _store.QuerySession();
        var slugs = await session.Query<Tenant>().Select(t => t.Slug).ToListAsync(ct);

        return slugs
            .Append(Tenant.DefaultSlug)
            .Append(JasperFx.StorageConstants.DefaultTenantId)
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Redacts every unredacted log in one partition. Pure over the session, so a test drives it directly.</summary>
    /// <returns>The number of logs that were rewritten.</returns>
    public static async Task<int> RedactStoredAsync(IDocumentSession session, CancellationToken ct)
    {
        var redacted = 0;
        var after = Guid.Empty;

        while (true)
        {
            // Keyset paging on the id. Skip would step over rows, since every row saved below drops
            // out of the filter, and re-reading the first page would walk the rows already done on
            // every batch.
            var batch = await session.Query<WorkflowExecutionLog>()
                .Where(l => l.MatchesSql(UnredactedSql + " and d.id > ?", after))
                .OrderBy(l => l.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;
            after = batch[^1].Id;

            var dirty = 0;
            foreach (var log in batch)
            {
                // A row whose key is missing reads Redacted false, so RedactStored changes it.
                if (!WorkflowDebugger.RedactStored(log)) continue;

                session.Store(log);
                dirty++;
            }

            if (dirty > 0)
            {
                await session.SaveChangesAsync(ct);
                redacted += dirty;
            }

            if (batch.Count < BatchSize) break;
        }

        return redacted;
    }
}
