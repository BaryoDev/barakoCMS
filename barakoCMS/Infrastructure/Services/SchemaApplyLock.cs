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

    /// <summary>
    /// Runs <paramref name="apply"/> with no other host applying the schema at the same time.
    /// </summary>
    /// <remarks>
    /// Blocking, not <c>pg_try_advisory_lock</c> as the retention sweeps use. A sweep that finds
    /// another instance working can skip its tick and lose nothing. A host that skipped the schema
    /// apply would carry on to the seeders against a database that may not have the tables yet,
    /// which is the failure this exists to prevent.
    ///
    /// Session scoped and held on its own connection, so a host that dies mid-apply frees the lock
    /// when the connection drops rather than wedging every later start. A transaction-scoped lock
    /// would not work here: Marten opens its own connections for the DDL, so there is no one
    /// transaction to attach to.
    /// </remarks>
    public static async Task RunAsync(IDocumentStore store, Func<Task> apply, CancellationToken ct = default)
    {
        // The connection object, not a new one built from its ConnectionString: Npgsql redacts the
        // password out of ConnectionString unless Persist Security Info is set, so rebuilding from
        // it fails authentication.
        await using var connection = store.Storage.Database.CreateConnection();
        await connection.OpenAsync(ct);

        await using (var acquire = connection.CreateCommand())
        {
            acquire.CommandText = "select pg_advisory_lock(@key)";
            acquire.Parameters.AddWithValue("key", Key);
            await acquire.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await apply();
        }
        finally
        {
            await using var release = connection.CreateCommand();
            release.CommandText = "select pg_advisory_unlock(@key)";
            release.Parameters.AddWithValue("key", Key);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }
}
