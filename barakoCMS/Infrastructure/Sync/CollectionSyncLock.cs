using System.Data;
using System.Globalization;
using Npgsql;

namespace barakoCMS.Infrastructure.Sync;

/// <summary>How long a run started from the API waits for a collection another run is filling.</summary>
internal sealed class CollectionSyncRunOptions
{
    /// <summary>Seconds, 5 by default, held between 0 and 30. 0 answers 409 at once.</summary>
    public const string LockWaitKey = "CollectionSyncs:RunLockWaitSeconds";

    /// <remarks>
    /// Long enough for the run in the way to finish when it is one sync of a handful of entries: a
    /// fetch and a commit per changed entry. Short enough that a request is not held open for a
    /// sync of hundreds.
    /// </remarks>
    public static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(5);

    /// <summary>The longest a request is ever held open waiting, whatever is configured.</summary>
    public static readonly TimeSpan MaxLockWait = TimeSpan.FromSeconds(30);

    public TimeSpan LockWait { get; set; } = DefaultLockWait;

    public static CollectionSyncRunOptions From(IConfiguration configuration)
    {
        var seconds = configuration.GetValue(LockWaitKey, DefaultLockWait.TotalSeconds);

        return new CollectionSyncRunOptions
        {
            LockWait = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, MaxLockWait.TotalSeconds)),
        };
    }
}

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
/// scoped advisory lock on a connection held open for the run. A process that dies mid-run frees it
/// when the connection drops. A transaction scoped lock would not do, because a run commits once
/// per entry.
///
/// The lock is only as good as its connection. If that connection is lost while the run is still
/// writing, the lock is gone and another run can start beside it.
/// </remarks>
internal sealed class CollectionSyncLock : IAsyncDisposable
{
    private const string Try = "select pg_try_advisory_lock(hashtextextended(@name, 0))";
    private const string Wait = "select pg_advisory_lock(hashtextextended(@name, 0))";
    private const string Release = "select pg_advisory_unlock(hashtextextended(@name, 0))";

    private readonly NpgsqlConnection _connection;
    private readonly string _name;
    private readonly ILogger _logger;

    private CollectionSyncLock(NpgsqlConnection connection, string name, ILogger logger)
    {
        _connection = connection;
        _name = name;
        _logger = logger;
    }

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

            if (await TakeAsync(connection, name, wait, ct))
            {
                return new CollectionSyncLock(connection, name, logger);
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

    /// <remarks>
    /// The wait is Postgres's own queue, not a loop of tries. The sweep takes the next sync of the
    /// same collection a few milliseconds after it lets go of the last, so a caller trying again
    /// every so often would lose to it each time. A queued waiter is handed the lock at the release.
    ///
    /// <c>lock_timeout</c> is set for one transaction, so it never outlives this on a pooled
    /// connection. A session lock taken inside a transaction stays held after it ends.
    /// </remarks>
    private static async Task<bool> TakeAsync(
        NpgsqlConnection connection, string name, TimeSpan wait, CancellationToken ct)
    {
        if (wait <= TimeSpan.Zero)
        {
            await using var once = connection.CreateCommand();
            once.CommandText = Try;
            once.Parameters.AddWithValue("name", name);

            return (bool?)await once.ExecuteScalarAsync(ct) ?? false;
        }

        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var limit = connection.CreateCommand())
        {
            limit.Transaction = transaction;
            limit.CommandText = "select set_config('lock_timeout', @milliseconds, true)";
            limit.Parameters.AddWithValue(
                "milliseconds", Math.Max(1, (long)wait.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
            await limit.ExecuteScalarAsync(ct);
        }

        try
        {
            await using var take = connection.CreateCommand();
            take.Transaction = transaction;
            take.CommandText = Wait;
            take.Parameters.AddWithValue("name", name);
            await take.ExecuteScalarAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return false;
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>Closes a connection that may hold the lock without it going back to a pool holding it.</summary>
    /// <remarks>
    /// A pooled connection closed with a session lock on it keeps the lock until the pool hands that
    /// physical connection to somebody else. If the lock cannot be released by asking, the physical
    /// connection is closed, which releases it.
    /// </remarks>
    private static async Task DropAsync(NpgsqlConnection connection, ILogger logger)
    {
        try
        {
            if (connection.State == ConnectionState.Open)
            {
                try
                {
                    await using var release = connection.CreateCommand();
                    release.CommandText = "select pg_advisory_unlock_all()";
                    await release.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "A collection sync lock could not be released; closing its connection instead");
                    NpgsqlConnection.ClearPool(connection);
                }
            }

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
            await using var release = _connection.CreateCommand();
            release.CommandText = Release;
            release.Parameters.AddWithValue("name", _name);
            await release.ExecuteScalarAsync(CancellationToken.None);

            await _connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The collection sync lock {Name} was not released cleanly", _name);
            await DropAsync(_connection, _logger);
        }
    }
}
