using Npgsql;

namespace barakoCMS.Infrastructure.Migrations;

/// <summary>What a ledger row or a status line says about one migration.</summary>
internal static class MigrationState
{
    /// <summary>The command ran the file.</summary>
    public const string Applied = "applied";

    /// <summary>Not run, because the database did not need it. See <see cref="MigrationLedger"/>.</summary>
    public const string Baselined = "baselined";

    /// <summary>Not run. An operator said it was applied by hand, with <c>--record</c>.</summary>
    public const string Recorded = "recorded";

    /// <summary>Status only: no row, and the next run applies it.</summary>
    public const string Pending = "pending";

    /// <summary>Status only: no row, and the next run records it as baselined without running it.</summary>
    public const string NotNeeded = "not-needed";

    /// <summary>
    /// Status only: the file was run or recorded here, and the row's checksum is not the shipped
    /// file's. A baselined row whose file changed keeps its own state: see <see cref="MigrationStatusLine.FileChanged"/>.
    /// </summary>
    public const string Changed = "changed";
}

internal sealed record LedgerRow(string Owner, string Id, string Checksum, string State, string RecordedAt)
{
    public string Key => $"{Owner}/{Id}";
}

internal sealed record MigrationStatusLine(ShippedMigration Migration, string State, LedgerRow? Row)
{
    /// <summary>The row was written for a file with different content than this build ships.</summary>
    public bool FileChanged => Row is not null && Row.Checksum != Migration.Checksum;
}

/// <param name="Lines">One line per shipped migration, in run order.</param>
/// <param name="NotShipped">Rows whose file this build does not ship: a module that is off, or a newer build's file.</param>
internal sealed record MigrationLedgerStatus(
    IReadOnlyList<MigrationStatusLine> Lines,
    IReadOnlyList<LedgerRow> NotShipped)
{
    /// <summary>Every shipped migration has a row that matches its file.</summary>
    public bool Current => Lines.All(l => l.Row is not null && l.State != MigrationState.Changed);

    public IReadOnlyList<MigrationStatusLine> In(string state) => Lines.Where(l => l.State == state).ToList();
}

internal enum MigrationRunOutcome
{
    Completed,
    LockBusy,
    ChecksumMismatch,
    Failed,
    Cancelled,
}

/// <param name="Applied">Keys of the files this run executed and recorded.</param>
/// <param name="Baselined">Keys this run recorded without executing.</param>
/// <param name="Changed">Rows whose checksum differs from the shipped file. Not empty means nothing ran.</param>
/// <param name="FailedKey">The migration the run stopped at, when it failed or was cancelled inside one.</param>
/// <param name="Error">What the database said about it.</param>
internal sealed record MigrationRunResult(
    MigrationRunOutcome Outcome,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Baselined,
    IReadOnlyList<MigrationStatusLine> Changed,
    string? FailedKey = null,
    string? Error = null);

/// <summary>
/// The record of which migrations a database has had, and the one place that runs them.
/// </summary>
/// <remarks>
/// <para>
/// One row per migration in <c>public.barako_migrations</c>, keyed by owner and id, with the
/// checksum of the file as it was when the row was written. The table name and every statement here
/// are constants. Owners and ids only ever travel as parameters.
/// </para>
/// <para>
/// A migration with no row is run, with two exceptions, and neither is silent: both write a
/// <c>baselined</c> row and say so.
/// </para>
/// <list type="bullet">
/// <item>The database has no <c>mt_doc_users</c> table. Nothing has started against it yet, and the
/// first start creates every object in its current shape, so no shipped migration applies to it.</item>
/// <item>The file carries a <c>skip-when</c> query and it answers true: the object the file creates is
/// already there, applied by hand before the ledger existed or created by the app.</item>
/// </list>
/// <para>
/// A run holds a session advisory lock on its own connection from before it reads the ledger until it
/// ends. A second run does not wait: it is told the lock is held and changes nothing.
/// </para>
/// <para>
/// A file that carries a skip query has to make it true. The query is asked again after the file
/// has run, and a false answer fails the file with no row. That is what catches an index build that
/// was interrupted: the invalid index it leaves makes <c>CREATE INDEX IF NOT EXISTS</c> a no-op.
/// </para>
/// </remarks>
internal sealed class MigrationLedger(
    Func<NpgsqlConnection> connect, string recordedBy, string schemaName = MigrationLedger.SupportedSchema)
{
    /// <summary>The schema the ledger table and every shipped file name.</summary>
    public const string SupportedSchema = "public";

    /// <summary>
    /// False for a store that keeps its tables somewhere other than <c>public</c>. Every check here
    /// looks in <c>public</c>, so on such a store an empty answer would mean nothing, and the command
    /// and the start both refuse to read anything into it.
    /// </summary>
    public bool SchemaSupported => string.Equals(schemaName, SupportedSchema, StringComparison.Ordinal);

    public string SchemaName => schemaName;

    /// <summary>Same family as <c>SchemaApplyLock.Key</c> and the sweep keys.</summary>
    public const long LockKey = 8_242_026_901L;

    private const string TryLockSql = "select pg_try_advisory_lock(@key)";
    private const string UnlockSql = "select pg_advisory_unlock(@key)";

    private const string CreateTableSql =
        """
        create table if not exists public.barako_migrations (
            owner       text        not null,
            id          text        not null,
            checksum    text        not null,
            state       text        not null,
            recorded_at timestamptz not null default now(),
            recorded_by text        not null,
            constraint pkey_barako_migrations primary key (owner, id)
        )
        """;

    private const string TableExistsSql = "select to_regclass('public.barako_migrations') is not null";

    // The users table is the one every start creates and nothing else does. Resolving the host's
    // services can create mt_doc_jobs on an empty database before any command runs, so "no tables at
    // all" would call a database used when it is not.
    private const string FreshSql = "select to_regclass('public.mt_doc_users') is null";

    private const string ReadSql =
        "select owner, id, checksum, state, to_char(recorded_at at time zone 'UTC', 'YYYY-MM-DD HH24:MI:SS') "
        + "from public.barako_migrations order by owner, id";

    private const string InsertSql =
        "insert into public.barako_migrations (owner, id, checksum, state, recorded_by) "
        + "values (@owner, @id, @checksum, @state, @by)";

    private const string InsertIfAbsentSql = InsertSql + " on conflict (owner, id) do nothing";

    private const string UpsertSql = InsertSql
        + " on conflict (owner, id) do update set checksum = excluded.checksum, state = excluded.state, "
        + "recorded_at = now(), recorded_by = excluded.recorded_by";

    private const string DeleteSql = "delete from public.barako_migrations where owner = @owner and id = @id";

    // Sessions of the application's own role are visible to this one, which connects as it does.
    private const string OpenTransactionsSql =
        "select count(*) from pg_stat_activity where datname = current_database() and pid <> pg_backend_pid() "
        + "and backend_type = 'client backend' and xact_start is not null";

    /// <summary>
    /// Runs every shipped migration the ledger lacks, in order, and stops at the first failure.
    /// </summary>
    /// <remarks>
    /// A transactional file and its ledger row commit together, so a crash or an error part way
    /// through leaves neither, and the next run starts that file from the top. A file marked
    /// <c>no-transaction</c> is executed first and recorded after: a crash between the two leaves the
    /// change without its row, so such a file has to be safe to run again.
    ///
    /// Nothing runs when a migration that was run or recorded here differs from the file this build
    /// ships. A baselined row whose file changed is reported and does not stop the run: that file
    /// never ran on this database and never will, so the edit cannot have split what ran from what
    /// is recorded.
    ///
    /// Cancelling <paramref name="ct"/> sends a cancel request to the server for the statement in
    /// flight, and the run ends as <see cref="MigrationRunOutcome.Cancelled"/>.
    /// </remarks>
    public async Task<MigrationRunResult> ApplyAsync(
        IReadOnlyList<ShippedMigration> shipped, Action<string> report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shipped);
        ArgumentNullException.ThrowIfNull(report);

        try
        {
            var (locked, result) = await UnderLockAsync(connection => ApplyLockedAsync(connection, shipped, report, ct), ct);
            return locked ? result! : new MigrationRunResult(MigrationRunOutcome.LockBusy, [], [], []);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Between files. A cancellation inside one is reported with its key, below.
            return new MigrationRunResult(MigrationRunOutcome.Cancelled, [], [], []);
        }
    }

    private async Task<MigrationRunResult> ApplyLockedAsync(
        NpgsqlConnection connection, IReadOnlyList<ShippedMigration> shipped, Action<string> report, CancellationToken ct)
    {
        await ExecuteAsync(connection, null, CreateTableSql, ct);
        var rows = await ReadRowsAsync(connection, ct);

        var changed = shipped
            .Where(m => rows.TryGetValue((m.Owner, m.Id), out var row) && row.Checksum != m.Checksum)
            .Select(m => new MigrationStatusLine(m, StateOf(rows[(m.Owner, m.Id)], m), rows[(m.Owner, m.Id)]))
            .ToList();
        var blocking = changed.Where(l => l.State == MigrationState.Changed).ToList();
        if (blocking.Count > 0)
            return new MigrationRunResult(MigrationRunOutcome.ChecksumMismatch, [], [], blocking);

        foreach (var line in changed)
        {
            report($"warning    {line.Migration.Key}  was baselined here and never run, and its file has changed since: "
                + $"recorded {line.Row!.Checksum}, this build ships {line.Migration.Checksum}. Nothing was done about it. "
                + $"db-migrate --record {line.Migration.Key} accepts the file as shipped.");
        }

        var fresh = await IsFreshAsync(connection, ct);
        var applied = new List<string>();
        var baselined = new List<string>();
        var lookedForOpenTransactions = false;

        foreach (var migration in shipped)
        {
            if (rows.ContainsKey((migration.Owner, migration.Id)))
                continue;

            try
            {
                if (fresh || await NotNeededAsync(connection, migration, ct))
                {
                    await WriteRowAsync(connection, null, InsertSql, migration, MigrationState.Baselined, ct);
                    baselined.Add(migration.Key);
                    report($"baselined  {migration.Key}  (not run: "
                        + (fresh ? "this database has not been started yet" : "its skip-when query found the change already in place")
                        + ")");
                    continue;
                }

                if (!lookedForOpenTransactions)
                {
                    lookedForOpenTransactions = true;
                    var open = await OpenTransactionsAsync(connection, ct);
                    if (open > 0)
                    {
                        report($"warning    {open} other session(s) have a transaction open on this database. Stop the API "
                            + "before migrating: a file waits on the locks they hold, and an index built CONCURRENTLY "
                            + "waits for every transaction older than itself, so it does not finish while one stays open.");
                    }
                }

                // psql prints what a file raises, and some files tell the operator something only that
                // way, so a run that kept it would be quieter than the hand route it replaces.
                void Relay(object sender, NpgsqlNoticeEventArgs e) =>
                    report($"notice     {migration.Key}  {e.Notice.MessageText}");

                connection.Notice += Relay;
                try
                {
                    await RunAsync(connection, migration, ct);
                }
                finally
                {
                    connection.Notice -= Relay;
                }

                applied.Add(migration.Key);
                report($"applied    {migration.Key}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new MigrationRunResult(MigrationRunOutcome.Cancelled, applied, baselined, [], migration.Key);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                // A cancel request can also come back as the server's own "canceling statement" error.
                return ct.IsCancellationRequested
                    ? new MigrationRunResult(MigrationRunOutcome.Cancelled, applied, baselined, [], migration.Key)
                    : new MigrationRunResult(MigrationRunOutcome.Failed, applied, baselined, [], migration.Key, Describe(ex));
            }
        }

        return new MigrationRunResult(MigrationRunOutcome.Completed, applied, baselined, []);
    }

    /// <summary>
    /// What the next <see cref="ApplyAsync"/> would find. Reads only: it takes no lock and does not
    /// create the ledger table.
    /// </summary>
    public async Task<MigrationLedgerStatus> StatusAsync(IReadOnlyList<ShippedMigration> shipped, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shipped);

        await using var connection = connect();
        await connection.OpenAsync(ct);

        var rows = await TableExistsAsync(connection, ct)
            ? await ReadRowsAsync(connection, ct)
            : new Dictionary<(string Owner, string Id), LedgerRow>();
        var fresh = await IsFreshAsync(connection, ct);

        var lines = new List<MigrationStatusLine>(shipped.Count);
        foreach (var migration in shipped)
        {
            if (rows.TryGetValue((migration.Owner, migration.Id), out var row))
            {
                lines.Add(new MigrationStatusLine(migration, StateOf(row, migration), row));
                continue;
            }

            bool notNeeded;
            try
            {
                notNeeded = fresh || await NotNeededAsync(connection, migration, ct);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                // A query that cannot answer has not shown the file is unneeded. The run reports the error.
                notNeeded = false;
            }

            lines.Add(new MigrationStatusLine(
                migration, notNeeded ? MigrationState.NotNeeded : MigrationState.Pending, null));
        }

        var shippedKeys = shipped.Select(m => (m.Owner, m.Id)).ToHashSet();
        var notShipped = rows.Values.Where(r => !shippedKeys.Contains((r.Owner, r.Id))).ToList();
        return new MigrationLedgerStatus(lines, notShipped);
    }

    /// <summary>
    /// Records every shipped migration as baselined when nothing has started against this database
    /// yet. Called by a host before it creates the schema, so the rows exist before the tables do and
    /// a crash between the two cannot leave a current schema that the ledger calls unmigrated.
    /// </summary>
    /// <returns>
    /// The keys recorded; empty when the database is not fresh; null when another run holds the lock.
    /// </returns>
    public async Task<IReadOnlyList<string>?> BaselineIfFreshAsync(
        IReadOnlyList<ShippedMigration> shipped, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shipped);

        if (!SchemaSupported)
            return [];

        await using (var probe = connect())
        {
            await probe.OpenAsync(ct);
            if (!await IsFreshAsync(probe, ct))
                return [];
        }

        var (locked, recorded) = await UnderLockAsync<IReadOnlyList<string>>(async connection =>
        {
            if (!await IsFreshAsync(connection, ct))
                return [];

            await ExecuteAsync(connection, null, CreateTableSql, ct);
            var keys = new List<string>(shipped.Count);
            foreach (var migration in shipped)
            {
                if (await WriteRowAsync(connection, null, InsertIfAbsentSql, migration, MigrationState.Baselined, ct) > 0)
                    keys.Add(migration.Key);
            }

            return keys;
        }, ct);

        return locked ? recorded : null;
    }

    /// <summary>
    /// Writes or replaces the row for a file an operator applied by hand, without running it.
    /// </summary>
    /// <returns>False when another run holds the lock.</returns>
    public async Task<bool> RecordAsync(ShippedMigration migration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(migration);

        var (locked, _) = await UnderLockAsync(async connection =>
        {
            await ExecuteAsync(connection, null, CreateTableSql, ct);
            return await WriteRowAsync(connection, null, UpsertSql, migration, MigrationState.Recorded, ct);
        }, ct);
        return locked;
    }

    /// <summary>Removes a row, so the next run treats the migration as one it has never seen.</summary>
    /// <returns>Rows removed, or null when another run holds the lock.</returns>
    public async Task<int?> ForgetAsync(string owner, string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var (locked, removed) = await UnderLockAsync(async connection =>
        {
            if (!await TableExistsAsync(connection, ct))
                return 0;

            await using var command = new NpgsqlCommand(DeleteSql, connection);
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("id", id);
            return await command.ExecuteNonQueryAsync(ct);
        }, ct);
        return locked ? removed : null;
    }

    private async Task<(bool Locked, T? Value)> UnderLockAsync<T>(
        Func<NpgsqlConnection, Task<T>> work, CancellationToken ct)
    {
        // Session scoped and on the connection the work runs on, so a run that dies frees the lock
        // when its connection drops, and the lock outlives each file's own transaction.
        await using var connection = connect();
        await connection.OpenAsync(ct);

        await using (var acquire = new NpgsqlCommand(TryLockSql, connection))
        {
            acquire.Parameters.AddWithValue("key", LockKey);
            if (await acquire.ExecuteScalarAsync(ct) is not true)
                return (false, default);
        }

        try
        {
            return (true, await work(connection));
        }
        finally
        {
            try
            {
                await using var release = new NpgsqlCommand(UnlockSql, connection);
                release.Parameters.AddWithValue("key", LockKey);
                await release.ExecuteScalarAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                // The connection is gone, and the lock went with it. Npgsql says so with either type,
                // and letting it out of this finally would replace the result that names the file.
            }
        }
    }

    private async Task RunAsync(NpgsqlConnection connection, ShippedMigration migration, CancellationToken ct)
    {
        if (!migration.Transactional)
        {
            await ExecuteFileAsync(connection, null, migration.Sql, ct);
            await RequireInPlaceAsync(connection, null, migration, ct);
            await WriteRowAsync(connection, null, InsertSql, migration, MigrationState.Applied, ct);
            return;
        }

        // Disposed without a commit, the transaction rolls back, which is what an exception does here.
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await ExecuteFileAsync(connection, transaction, migration.Sql, ct);
        await RequireInPlaceAsync(connection, transaction, migration, ct);
        await WriteRowAsync(connection, transaction, InsertSql, migration, MigrationState.Applied, ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task RequireInPlaceAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, ShippedMigration migration, CancellationToken ct)
    {
        if (migration.SkipWhen is null)
            return;

        await using var command = new NpgsqlCommand(migration.SkipWhen, connection, transaction);
        if (await command.ExecuteScalarAsync(ct) is true)
            return;

        throw new InvalidOperationException(
            "the file ran and its skip-when query still answers false, so its change is not in place and nothing "
            + "was recorded. If the file builds an index CONCURRENTLY, an earlier build that was interrupted has "
            + "left an invalid index under that name, which IF NOT EXISTS then skips: drop that index and run "
            + "db-migrate again. See docs/migrations.md.");
    }

    private static string StateOf(LedgerRow row, ShippedMigration migration) =>
        row.Checksum == migration.Checksum || row.State == MigrationState.Baselined
            ? row.State
            : MigrationState.Changed;

    private static async Task<long> OpenTransactionsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(OpenTransactionsSql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> NotNeededAsync(NpgsqlConnection connection, ShippedMigration migration, CancellationToken ct)
    {
        if (migration.SkipWhen is null)
            return false;

        // Read only and rolled back: the query is there to look, and a file that used it to change
        // something would be doing so outside the ledger.
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await ExecuteAsync(connection, transaction, "set transaction read only", ct);

        await using var command = new NpgsqlCommand(migration.SkipWhen, connection, transaction);
        var answer = await command.ExecuteScalarAsync(ct);
        await transaction.RollbackAsync(ct);

        return answer is bool notNeeded
            ? notNeeded
            : throw new InvalidOperationException($"The skip-when query of {migration.Key} has to return one boolean.");
    }

    private static async Task ExecuteFileAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        // An index build or a backfill has no fixed length, and Npgsql's 30 second default would cut
        // one off part way. The bound is the caller's token: cancelling it makes Npgsql send the
        // server a cancel request for this statement.
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> WriteRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        ShippedMigration migration,
        string state,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("owner", migration.Owner);
        command.Parameters.AddWithValue("id", migration.Id);
        command.Parameters.AddWithValue("checksum", migration.Checksum);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("by", recordedBy);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> IsFreshAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(FreshSql, connection);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(TableExistsSql, connection);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<Dictionary<(string Owner, string Id), LedgerRow>> ReadRowsAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        var rows = new Dictionary<(string Owner, string Id), LedgerRow>();
        await using var command = new NpgsqlCommand(ReadSql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new LedgerRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
            rows[(row.Owner, row.Id)] = row;
        }

        return rows;
    }

    private static string Describe(Exception ex) => ex is PostgresException pg
        ? $"{pg.SqlState}: {pg.MessageText}" + (string.IsNullOrEmpty(pg.Detail) ? string.Empty : $" ({pg.Detail})")
        : ex.Message;
}
