using Marten;
using barakoCMS.Infrastructure.Multitenancy;

namespace barakoCMS.Infrastructure.Retention;

/// <summary>What one pass of <see cref="RetentionSweeper"/> did.</summary>
/// <param name="LockHeld">True when this node ran the pass, false when another node held the lock.</param>
/// <param name="Removed">Rows deleted across all declarations and tenants.</param>
/// <param name="TenantsSwept">Partitions that held something to look at.</param>
/// <param name="TenantsSkipped">Partitions listed but empty, left unopened beyond one existence check.</param>
internal readonly record struct RetentionSweepResult(bool LockHeld, int Removed, int TenantsSwept, int TenantsSkipped);

/// <summary>
/// The one place that holds a retention advisory lock, walks the tenant partitions, deletes what
/// the declarations say is past its window, and logs the total.
/// </summary>
/// <remarks>
/// Three services used to carry their own copy of this. What each now supplies is a list of
/// <see cref="RetentionDeclaration"/> and, if it needs one, a step to run in a partition before the
/// deletes. Deleting by predicate is what lets a later change swap a declaration for a partition
/// drop without touching the services (#622).
/// </remarks>
internal static class RetentionSweeper
{
    /// <summary>
    /// Runs one pass if no other node holds <paramref name="lockKey"/>.
    /// </summary>
    /// <param name="partitionsSql">The <c>select distinct tenant_id</c> used when partitions come from the rows.</param>
    /// <param name="beforeDeletes">Optional work in a partition before its deletes, returning rows it changed.</param>
    public static async Task<RetentionSweepResult> TrySweepAllTenantsAsync(
        IDocumentStore store,
        IConfiguration config,
        long lockKey,
        string partitionsSql,
        IReadOnlyList<RetentionDeclaration> declarations,
        DateTimeOffset nowUtc,
        ILogger logger,
        string label,
        CancellationToken ct,
        Func<IDocumentSession, CancellationToken, Task<int>>? beforeDeletes = null)
    {
        // The connection object rather than one rebuilt from its ConnectionString, because Npgsql
        // redacts the password out of that string unless Persist Security Info is set.
        await using var lockConnection = store.Storage.Database.CreateConnection();
        await lockConnection.OpenAsync(ct);

        await using (var acquire = lockConnection.CreateCommand())
        {
            acquire.CommandText = "select pg_try_advisory_lock(@key)";
            acquire.Parameters.AddWithValue("key", lockKey);

            if ((bool?)await acquire.ExecuteScalarAsync(ct) is not true)
            {
                logger.LogDebug("Another instance is sweeping {Label}; skipping this tick.", label);
                return new RetentionSweepResult(false, 0, 0, 0);
            }
        }

        try
        {
            var removed = 0;
            var swept = 0;
            var skipped = 0;

            foreach (var tenantId in await TenantPartitions.ListAsync(store, config, partitionsSql, ct))
            {
                await using var session = store.LightweightSession(tenantId);

                if (!await AnyRowsAsync(session, declarations, ct))
                {
                    skipped++;
                    continue;
                }

                swept++;
                if (beforeDeletes is not null) removed += await beforeDeletes(session, ct);
                removed += await SweepSessionAsync(session, tenantId, declarations, nowUtc, ct);
            }

            if (removed > 0)
            {
                logger.LogInformation("{Label} retention removed {Count} row(s)", label, removed);
            }

            return new RetentionSweepResult(true, removed, swept, skipped);
        }
        finally
        {
            try
            {
                await using var release = lockConnection.CreateCommand();
                release.CommandText = "select pg_advisory_unlock(@key)";
                release.Parameters.AddWithValue("key", lockKey);
                await release.ExecuteScalarAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or ObjectDisposedException)
            {
                // The lock connection died mid-sweep, which already freed the lock. A delete that
                // runs twice is a no-op, so a second instance sweeping meanwhile does no harm.
                logger.LogWarning(ex, "The {Label} retention lock connection was lost before the sweep finished", label);
            }
        }
    }

    /// <summary>Runs every declaration against one session's partition. No lock, no tenant walk.</summary>
    public static async Task<int> SweepSessionAsync(
        IDocumentSession session,
        string tenantId,
        IReadOnlyList<RetentionDeclaration> declarations,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var removed = 0;
        foreach (var declaration in declarations)
        {
            removed += await declaration.SweepAsync(session, tenantId, nowUtc, ct);
        }

        return removed;
    }

    private static async Task<bool> AnyRowsAsync(
        IQuerySession session, IReadOnlyList<RetentionDeclaration> declarations, CancellationToken ct)
    {
        foreach (var declaration in declarations)
        {
            if (await declaration.HasRowsAsync(session, ct)) return true;
        }

        return false;
    }
}
