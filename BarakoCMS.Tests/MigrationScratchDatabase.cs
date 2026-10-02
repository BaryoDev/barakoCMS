using barakoCMS.Infrastructure.Migrations;
using Npgsql;

namespace BarakoCMS.Tests;

/// <summary>
/// A database of its own on the fixture's server, for a test that runs migrations.
/// </summary>
/// <remarks>
/// The ledger is one fixed table and the lock is one fixed key, both per database, so a test that
/// ran against the shared fixture database would read the rows the fixture's own start wrote and
/// would hold up every other host. <c>public.runs</c> is what the test migrations write to: one row
/// per time a file really ran.
/// </remarks>
internal sealed class MigrationScratchDatabase : IAsyncDisposable
{
    private readonly string _server;
    private readonly string _name;

    private MigrationScratchDatabase(string server, string name)
    {
        _server = server;
        _name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
    }

    public string ConnectionString { get; }

    /// <param name="fixture">Whose server to create the database on.</param>
    /// <param name="started">
    /// True gives the database a users table, which is how the ledger tells a database a host has
    /// started against from one nothing has touched.
    /// </param>
    public static async Task<MigrationScratchDatabase> CreateAsync(IntegrationTestFixture fixture, bool started = true)
    {
        var name = "migr_" + Guid.NewGuid().ToString("n")[..12];
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"create database {name}", connection);
            await create.ExecuteNonQueryAsync();
        }

        var database = new MigrationScratchDatabase(fixture.ConnectionString, name);
        await database.ExecuteAsync("create table public.runs (name text not null)");
        if (started)
            await database.ExecuteAsync("create table public.mt_doc_users (id uuid primary key)");
        return database;
    }

    public MigrationLedger Ledger() => new(() => new NpgsqlConnection(ConnectionString), "tests");

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> IsTrueAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync() is true;
    }

    /// <summary>The names in <c>public.runs</c>, in the order they were written.</summary>
    public Task<List<string>> RunsAsync() => ReadAsync("select name from public.runs order by ctid");

    /// <summary>Each ledger row as <c>owner/id state</c>, or nothing when the table is not there.</summary>
    public async Task<List<string>> LedgerAsync() =>
        await IsTrueAsync("select to_regclass('public.barako_migrations') is not null")
            ? await ReadAsync("select owner || '/' || id || ' ' || state from public.barako_migrations order by owner, id")
            : [];

    private async Task<List<string>> ReadAsync(string sql)
    {
        var values = new List<string>();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_server);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"drop database if exists {_name} with (force)", connection);
        await drop.ExecuteNonQueryAsync();
    }
}
