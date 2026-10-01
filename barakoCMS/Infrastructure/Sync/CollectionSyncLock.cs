using Marten;
using Npgsql;

namespace barakoCMS.Infrastructure.Sync;

/// <summary>
/// Lets one run at a time fill a collection, whether the sweep started it or somebody pressed run.
/// </summary>
/// <remarks>
/// A run reads an entry and then writes it, and two runs doing that for the same key both find no
/// entry and both start its stream. The second one fails on the stream's primary key.
///
/// Keyed on the tenant and the content type, not on the sync, because that is what an entry's id is
/// derived from: two syncs filling one collection with the same key write the same entry.
///
/// The same mechanism as the sweep's own lock in <see cref="CollectionSyncService"/>: a session
/// scoped advisory lock, tried rather than waited for, on a connection held open for the run. A
/// process that dies mid-run frees it when the connection drops. A transaction scoped lock would not
/// do, because a run commits once per entry.
/// </remarks>
internal sealed class CollectionSyncLock : IAsyncDisposable
{
    private const string Acquire = "select pg_try_advisory_lock(hashtextextended(@name, 0))";
    private const string Release = "select pg_advisory_unlock(hashtextextended(@name, 0))";

    private readonly NpgsqlConnection _connection;
    private readonly string _name;

    private CollectionSyncLock(NpgsqlConnection connection, string name)
    {
        _connection = connection;
        _name = name;
    }

    /// <summary>The text the lock key is hashed from.</summary>
    /// <remarks>Lower case, as the entry id is, so <c>Package</c> and <c>package</c> are one collection.</remarks>
    internal static string NameFor(string tenantId, string contentType) =>
        $"barakocms:collection-sync:{tenantId}:{contentType.ToLowerInvariant()}";

    /// <summary>Takes the lock, or returns null when another run of this collection holds it.</summary>
    public static async Task<CollectionSyncLock?> TryAcquireAsync(
        IDocumentStore store, string tenantId, string contentType, CancellationToken ct)
    {
        var name = NameFor(tenantId, contentType);

        // The connection object, not a new one built from its ConnectionString: Npgsql redacts the
        // password out of ConnectionString unless Persist Security Info is set.
        var connection = store.Storage.Database.CreateConnection();

        try
        {
            await connection.OpenAsync(ct);

            await using var acquire = connection.CreateCommand();
            acquire.CommandText = Acquire;
            acquire.Parameters.AddWithValue("name", name);

            if ((bool?)await acquire.ExecuteScalarAsync(ct) ?? false)
            {
                return new CollectionSyncLock(connection, name);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var release = _connection.CreateCommand();
            release.CommandText = Release;
            release.Parameters.AddWithValue("name", _name);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}
