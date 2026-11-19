using System.Data;
using barakoCMS.Infrastructure.Services;
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
/// scoped advisory lock taken through <see cref="AdvisoryLease"/> on a connection held open for the
/// run. A process that dies mid-run frees it when the connection drops. A transaction scoped lock
/// would not do, because a run commits once per entry.
///
/// The lock is only as good as its connection. If that connection is lost while the run is still
/// writing, the lock is gone and another run can start beside it. <see cref="IsHeldAsync"/> asks
/// whether it is still there, and the sweep and the run endpoint ask before they start a run.
///
/// The key is <c>hashtextextended(name, 0)</c>, worked out by the server, so it is the one earlier
/// versions took with raw SQL and two versions overlapping during a rolling deploy exclude each other.
/// </remarks>
internal sealed class CollectionSyncLock : IAsyncDisposable
{
    private const string KeySql = "select hashtextextended(@name, 0)";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly NpgsqlConnection _connection;
    private readonly AdvisoryLease _lease;
    private readonly string _name;
    private readonly ILogger _logger;

    private CollectionSyncLock(NpgsqlConnection connection, AdvisoryLease lease, string name, ILogger logger)
    {
        _connection = connection;
        _lease = lease;
        _name = name;
        _logger = logger;
    }

    /// <summary>Tests only: runs once the lock is held, with its name, before the caller gets it.</summary>
    internal static Func<string, CancellationToken, Task>? AfterLockHeld { get; set; }

    /// <summary>Tests only: runs each time a try finds the lock held, so a test can tell a run is waiting.</summary>
    internal static Action<string>? OnRefused { get; set; }

    /// <summary>
    /// Whether this run still owns the lock. False when its connection was lost, which freed the lock
    /// and let another run start. Always true behind a suspected pooler, where it cannot be checked.
    /// </summary>
    public Task<bool> IsHeldAsync(CancellationToken ct) => _lease.IsHeldAsync(ct);

    /// <summary>The text the lock key is hashed from.</summary>
    /// <remarks>Lower case, as the entry id is, so <c>Package</c> and <c>package</c> are one collection.</remarks>
    internal static string NameFor(string tenantId, string contentType) =>
        $"barakocms:collection-sync:{tenantId}:{contentType.ToLowerInvariant()}";

    /// <summary>
    /// Takes the lock, or returns null when another run of this collection still holds it after
    /// <paramref name="wait"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="connect"/> makes the connection the lock lives on. Pass the store's own
    /// connection object, not one built from its ConnectionString: Npgsql redacts the password out of
    /// that unless Persist Security Info is set. A <paramref name="wait"/> of zero tries once, which
    /// is what the sweep does.
    /// </remarks>
    public static async Task<CollectionSyncLock?> TryAcquireAsync(
        Func<NpgsqlConnection> connect,
        string tenantId,
        string contentType,
        TimeSpan wait,
        ILogger logger,
        CancellationToken ct)
    {
        var name = NameFor(tenantId, contentType);
        var connection = connect();

        try
        {
            await connection.OpenAsync(ct);

            long key;
            await using (var hash = connection.CreateCommand())
            {
                hash.CommandText = KeySql;
                hash.Parameters.AddWithValue("name", name);
                key = Convert.ToInt64(await hash.ExecuteScalarAsync(ct));
            }

            // Polls pg_try_advisory_lock, the way SchemaApplyLock does, so a holder that is wedged
            // ends the wait as a refusal. The token ends it early.
            var started = System.Diagnostics.Stopwatch.StartNew();
            AdvisoryLease? lease;
            while ((lease = await AdvisoryLease.TryAcquireAsync(connection, key, ct)) is null)
            {
                OnRefused?.Invoke(name);

                var left = wait - started.Elapsed;
                if (left <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(left < PollInterval ? left : PollInterval, ct);
            }

            if (lease is not null)
            {
                if (lease.PoolerSuspected)
                {
                    AdvisoryLease.ReportPooler(logger, key);
                }

                if (AfterLockHeld is { } hook)
                {
                    await hook(name, ct);
                }

                return new CollectionSyncLock(connection, lease, name, logger);
            }
        }
        catch
        {
            // The server may have granted the lock and the answer never arrived, a cancellation
            // landing as it was granted for instance.
            await DropAsync(connection, logger);
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    /// <summary>Closes a connection that may hold the lock, releasing whatever it holds first.</summary>
    /// <remarks>
    /// A pooled connection closed with a session lock on it keeps the lock until the pool hands that
    /// physical connection to somebody else, so the lock is released by asking before the close.
    /// The rollback is for a connection left in a failed transaction, where every other statement
    /// is refused.
    ///
    /// If that fails as well the connection is almost always broken, and Npgsql closes a broken
    /// connection instead of pooling it, which frees the lock. Should it be pooled all the same, the
    /// lock lasts until that physical connection is reused or closed. There is no call that closes
    /// one connection of a data source.
    /// </remarks>
    private static async Task DropAsync(NpgsqlConnection connection, ILogger logger)
    {
        try
        {
            if (connection.State == ConnectionState.Open)
            {
                await using var release = connection.CreateCommand();
                release.CommandText = "rollback; select pg_advisory_unlock_all()";
                await release.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A collection sync lock could not be released before its connection closed");
        }

        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A collection sync lock's connection did not close cleanly");
        }
    }

    /// <remarks>
    /// Never throws. The run this lock covered has already finished, and its outcome is the answer:
    /// an unlock that fails must not turn it into an error, or stop the sweep's other syncs.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!await _lease.ReleaseAsync())
            {
                // The connection was gone or refused the unlock, so it is closed the careful way.
                await DropAsync(_connection, _logger);
                return;
            }

            await _lease.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The collection sync lock {Name} was not released cleanly", _name);
            await DropAsync(_connection, _logger);
        }
    }
}
