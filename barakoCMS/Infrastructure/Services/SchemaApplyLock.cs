using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Serialises schema creation across hosts starting against the same database at the same time.
/// </summary>
/// <remarks>
/// <para>
/// Marten decides what to create by asking the database what exists and then issuing the DDL. Those
/// two steps are not atomic, so two hosts starting together both see an object missing, both create
/// it, and the loser's whole DDL batch fails:
/// </para>
/// <code>
/// Npgsql.PostgresException : 42P07: relation "mt_doc_jobs_idx_queue_id" already exists
/// </code>
/// <para>
/// Marten already solves this for its own <c>ApplyAllDatabaseChangesOnStartup</c>, through
/// <c>StoreOptions.ApplyChangesLockId</c>. This application cannot use that option, because the
/// schema has to be applied before the data seeders rather than by a boot-time hosted service, so
/// the lock came off with it. This puts it back on the path that replaced it. See issue #609.
/// </para>
/// <para>
/// It matters in production, where two instances of a deployment start together, and in the test
/// suite, where it was noticed: a racing host failed a <c>Backend (.NET)</c> job, and because the
/// merge queue merges a group only if the whole group is green, one racing host sent every entry in
/// the batch back to its branch.
/// </para>
/// </remarks>
internal static class SchemaApplyLock
{
    /// <summary>
    /// The advisory lock key. Same family as the sweep keys in <c>ScheduledContentService</c> (key ...001)
    /// and <c>WorkflowRunRetentionService</c> (key ...002).
    /// </summary>
    public const long Key = 8_242_026_003L;

    /// <summary>The wait before giving up on the lock when nothing configures it.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Configuration key, in seconds, for how long a host waits for the schema lock.</summary>
    public const string TimeoutSecondsKey = "Schema:ApplyLockTimeoutSeconds";

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>The configured wait, or the default when unset or not a positive number of seconds.</summary>
    public static TimeSpan TimeoutFrom(IConfiguration? configuration) =>
        configuration?.GetValue<int?>(TimeoutSecondsKey) is > 0 and var seconds
            ? TimeSpan.FromSeconds(seconds)
            : DefaultTimeout;

    /// <summary>
    /// Runs <paramref name="apply"/> with no other host applying the schema at the same time.
    /// </summary>
    /// <remarks>
    /// Waits for the lock, but not forever. It polls <c>pg_try_advisory_lock</c> rather than blocking
    /// in <c>pg_advisory_lock</c>: a blocked call can sit behind a holder that is wedged, or behind a
    /// pooler that never freed the lock, and startup hangs with no message. A host that skipped the
    /// schema apply would carry on to the seeders against a database that may not have the tables
    /// yet, which is the failure this exists to prevent, so running out of time throws and the host
    /// does not start.
    ///
    /// Session scoped and held on its own connection, so a host that dies mid-apply frees the lock
    /// when the connection drops rather than wedging every later start. A transaction-scoped lock
    /// would not work here: Marten opens its own connections for the DDL, so there is no one
    /// transaction to attach to. The connection is used only to take and release the lock, in
    /// sequence, never while a command is running on it.
    /// </remarks>
    public static Task RunAsync(IDocumentStore store, Func<Task> apply, CancellationToken ct = default) =>
        RunAsync(store, apply, DefaultTimeout, DefaultPollInterval, null, ct);

    /// <inheritdoc cref="RunAsync(IDocumentStore, Func{Task}, CancellationToken)"/>
    /// <param name="configuration">Read for the schema lock timeout and <c>Leadership:RefuseBehindPooler</c>.</param>
    public static Task RunAsync(IDocumentStore store, Func<Task> apply, IConfiguration? configuration, CancellationToken ct = default) =>
        RunAsync(store, apply, TimeoutFrom(configuration), DefaultPollInterval, configuration, ct);

    /// <inheritdoc cref="RunAsync(IDocumentStore, Func{Task}, CancellationToken)"/>
    public static Task RunAsync(IDocumentStore store, Func<Task> apply, TimeSpan timeout, CancellationToken ct = default) =>
        RunAsync(store, apply, timeout, DefaultPollInterval, null, ct);

    internal static async Task RunAsync(
        IDocumentStore store, Func<Task> apply, TimeSpan timeout, TimeSpan pollInterval,
        IConfiguration? configuration, CancellationToken ct)
    {
        // The connection object, not a new one built from its ConnectionString: Npgsql redacts the
        // password out of ConnectionString unless Persist Security Info is set, so rebuilding from
        // it fails authentication.
        var connection = store.Storage.Database.CreateConnection();

        AdvisoryLease? lease = null;
        try
        {
            await connection.OpenAsync(ct);
            var started = System.Diagnostics.Stopwatch.StartNew();
            while ((lease = await AdvisoryLease.TryAcquireAsync(connection, Key, ct)) is null)
            {
                if (started.Elapsed >= timeout)
                {
                    var holder = await HolderPidAsync(connection, ct);
                    throw new TimeoutException(
                        $"Timed out after {timeout.TotalSeconds:0}s waiting for the schema apply lock "
                        + $"(advisory lock key {Key}), held by Postgres backend pid "
                        + $"{(holder is { } pid ? pid.ToString() : "unknown")}. Another host is applying the schema, "
                        + "or a connection that held it was never closed. A pooler in transaction mode can leave "
                        + $"it held: connect directly or use session pooling. Raise {TimeoutSecondsKey} to wait longer.");
                }

                await Task.Delay(pollInterval, ct);
            }

            if (lease.PoolerSuspected && AdvisoryLease.RefuseBehindPooler(configuration))
            {
                throw new InvalidOperationException(AdvisoryLease.PoolerMessage(Key));
            }

            await apply();
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }
            else
            {
                await connection.DisposeAsync();
            }
        }
    }

    private static async Task<int?> HolderPidAsync(Npgsql.NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "select pid from pg_locks where locktype = 'advisory' and granted "
            + "and ((classid::bigint << 32) | objid::bigint) = @key limit 1";
        command.Parameters.AddWithValue("key", Key);
        return await command.ExecuteScalarAsync(ct) as int?;
    }
}
