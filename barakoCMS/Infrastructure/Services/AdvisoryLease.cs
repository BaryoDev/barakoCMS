using Npgsql;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// A session advisory lock on its own connection, with a way to ask whether it is still held and a
/// check for a transaction-mode pooler. Shared by the two sweeps and the schema apply lock.
/// </summary>
/// <remarks>
/// Postgres frees a session lock when its backend goes away (network blip, idle cull, failover), and
/// nothing tells the holder. <see cref="IsHeldAsync"/> asks the server whether this backend still
/// owns the lock, so a sweep can stop between units of work instead of carrying on as a second
/// leader. Every use of the connection goes through one gate, so a check never overlaps the release
/// (Npgsql allows one operation at a time on a connection, and an overlap hangs the close).
/// </remarks>
internal sealed class AdvisoryLease : IAsyncDisposable
{
    /// <summary>Config key. When true, a node that detects a transaction-mode pooler declines the lock.</summary>
    public const string RefuseBehindPoolerKey = "Leadership:RefuseBehindPooler";

    private readonly NpgsqlConnection _connection;
    private readonly long _key;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _released;

    private AdvisoryLease(NpgsqlConnection connection, long key, bool pooledSuspected)
    {
        _connection = connection;
        _key = key;
        PoolerSuspected = pooledSuspected;
    }

    /// <summary>
    /// True when two statements on the lock connection ran on different server backends, which a
    /// session lock cannot survive: the pooler is in transaction mode.
    /// </summary>
    public bool PoolerSuspected { get; }

    /// <summary>The lock was taken on <paramref name="connection"/>, which the lease now owns.</summary>
    /// <returns>The lease, or null when another session holds the lock.</returns>
    public static async Task<AdvisoryLease?> TryAcquireAsync(NpgsqlConnection connection, long key, CancellationToken ct)
    {
        int firstPid;
        await using (var acquire = connection.CreateCommand())
        {
            acquire.CommandText = "select pg_try_advisory_lock(@key), pg_backend_pid()";
            acquire.Parameters.AddWithValue("key", key);
            await using var reader = await acquire.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            if (!reader.GetBoolean(0))
            {
                return null;
            }

            firstPid = reader.GetInt32(1);
        }

        await using var pid = connection.CreateCommand();
        pid.CommandText = "select pg_backend_pid()";
        var secondPid = Convert.ToInt32(await pid.ExecuteScalarAsync(ct));
        return new AdvisoryLease(connection, key, firstPid != secondPid);
    }

    /// <summary>
    /// Whether this backend still owns the lock. False when the lock is gone or the connection is.
    /// Always true behind a suspected pooler, where it cannot be checked.
    /// </summary>
    public async Task<bool> IsHeldAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_released)
            {
                return false;
            }

            if (PoolerSuspected)
            {
                // The backend changes under the connection, so ownership cannot be read back.
                return true;
            }

            await using var check = _connection.CreateCommand();
            check.CommandText =
                "select count(*) from pg_locks where locktype = 'advisory' and granted "
                + "and pid = pg_backend_pid() and ((classid::bigint << 32) | objid::bigint) = @key";
            check.Parameters.AddWithValue("key", _key);
            return Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Frees the lock. Returns false when the connection was already gone, which freed it.</summary>
    public async Task<bool> ReleaseAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_released)
            {
                return true;
            }

            _released = true;
            await using var release = _connection.CreateCommand();
            release.CommandText = "select pg_advisory_unlock(@key)";
            release.Parameters.AddWithValue("key", _key);
            await release.ExecuteScalarAsync(CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync();
        await _connection.DisposeAsync();
        _gate.Dispose();
    }

    /// <summary>The configured refusal, off unless <see cref="RefuseBehindPoolerKey"/> is true.</summary>
    public static bool RefuseBehindPooler(IConfiguration? configuration) =>
        configuration?.GetValue<bool?>(RefuseBehindPoolerKey) == true;

    /// <summary>The message logged, or thrown, when a transaction-mode pooler is suspected.</summary>
    public static string PoolerMessage(long key) =>
        $"Two statements on the advisory lock connection (key {key}) ran on different Postgres backends, so a "
        + "pooler in transaction mode sits between this host and the database. Session advisory locks do not "
        + "hold there: every instance can believe it is the leader. Connect directly or use session pooling. "
        + $"Set {RefuseBehindPoolerKey} to true to stop sweeping instead of carrying on.";
}
