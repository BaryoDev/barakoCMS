using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// The hand-applied changed-since index has to be the index Marten would have created.
/// </summary>
/// <remarks>
/// `workflow_runs` exists on every deployed instance and production runs `AutoCreate.CreateOnly`, so
/// the (UpdatedAt, id) index core declares lands on a fresh database only. An upgraded one needs
/// `migrations/5.1.0/workflow-run-updated-at-index.sql`, and a name or an expression written from
/// memory makes every start-up schema assertion ask to drop and recreate it.
///
/// The file is run against a copy of the table in a scratch schema, and the index Postgres reports
/// for it is compared with the one it reports for the table Marten built. Both come back in
/// Postgres's own canonical form, so spacing and optional parentheses in the file are not a
/// difference and a different expression, column or name is.
/// </remarks>
[Collection("Integration1")]
public class WorkflowRunUpdatedAtIndexMigrationTests
{
    private const string Table = "mt_doc_workflow_runs";
    private const string Index = "mt_doc_workflow_runs_idx_updated_at_id";
    private const string Up = "workflow-run-updated-at-index.sql";
    private const string Down = "rollback-workflow-run-updated-at-index.sql";

    private readonly IntegrationTestFixture _factory;

    public WorkflowRunUpdatedAtIndexMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_migration_builds_the_index_Marten_creates()
    {
        await EnsureTableAsync();
        var scratch = NewScratchName();
        var up = Scoped(await MigrationAsync(Up), scratch);

        await using var connection = await OpenAsync();
        try
        {
            var live = await IndexesAsync(connection, "public");
            live.Should().HaveCount(1, "core declares the (UpdatedAt, id) index under this name");

            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}; CREATE TABLE {scratch}.{Table} (LIKE public.{Table} INCLUDING DEFAULTS);");
            await ExecuteAsync(connection, up);

            var built = await IndexesAsync(connection, scratch);
            built.Should().HaveCount(1);
            built[0].Replace($"{scratch}.", "public.").Should().Be(live[0],
                "the file has to build the index Marten builds, or a start-up schema assertion asks to drop and recreate it");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    [Fact]
    public async Task The_migration_can_run_twice_and_the_rollback_drops_the_index()
    {
        await EnsureTableAsync();
        var scratch = NewScratchName();
        var up = Scoped(await MigrationAsync(Up), scratch);
        var down = Scoped(await MigrationAsync(Down), scratch);

        await using var connection = await OpenAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}; CREATE TABLE {scratch}.{Table} (LIKE public.{Table} INCLUDING DEFAULTS);");

            await ExecuteAsync(connection, up);
            await ExecuteAsync(connection, up);
            (await IndexesAsync(connection, scratch)).Should().HaveCount(1);

            await ExecuteAsync(connection, down);
            await ExecuteAsync(connection, down);
            (await IndexesAsync(connection, scratch)).Should().BeEmpty();
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    private static string NewScratchName() => "wfrun_index_" + Guid.NewGuid().ToString("N")[..8];

    private async Task EnsureTableAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await store.Storage.Database.EnsureStorageExistsAsync(typeof(WorkflowRun), Ct);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<List<string>> IndexesAsync(NpgsqlConnection connection, string schema)
    {
        var found = new List<string>();
        await using var command = new NpgsqlCommand(
            "select indexdef from pg_indexes where schemaname = @schema and tablename = @table and indexname = @index",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", Table);
        command.Parameters.AddWithValue("index", Index);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            found.Add(reader.GetString(0));
        return found;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>The file with the table, and so the index named after it, moved into a scratch schema.</summary>
    private static string Scoped(string sql, string scratch)
    {
        var scoped = sql.Replace($"public.{Table}", $"{scratch}.{Table}");
        scoped.Should().NotBe(sql, "the file should name the table as public.{0}", Table);
        return scoped;
    }

    private static async Task<string> MigrationAsync(string file) =>
        await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", "5.1.0", file), Ct);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test binary should sit under the repository");
        return directory!.FullName;
    }
}
