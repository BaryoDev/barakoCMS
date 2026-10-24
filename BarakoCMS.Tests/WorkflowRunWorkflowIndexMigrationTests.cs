using FluentAssertions;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The hand-applied index file has to build the index Marten builds on workflow_runs.
/// </summary>
/// <remarks>
/// Production runs <c>AutoCreate.CreateOnly</c>, which never alters a table that exists, so an
/// upgraded database gets the (WorkflowDefinitionId, UpdatedAt, Id) index from
/// <c>migrations/5.1.0/workflow-run-workflow-index.sql</c> or not at all. Applies the file to a copy of
/// the table in a scratch schema and compares the index with the one Marten created on the fixture's
/// database. The comparison is on what Postgres reports, not on the text of either side.
/// </remarks>
[Collection("Integration1")]
public class WorkflowRunWorkflowIndexMigrationTests
{
    private const string Table = "mt_doc_workflow_runs";
    private const string Index = "mt_doc_workflow_runs_idx_workflow_updated_at";

    private readonly IntegrationTestFixture _factory;

    public WorkflowRunWorkflowIndexMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task The_hand_applied_migration_builds_the_index_Marten_builds()
    {
        var ct = TestContext.Current.CancellationToken;
        var sql = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "migrations", "5.1.0", "workflow-run-workflow-index.sql"), ct);

        var scratch = "workflow_index_check_" + Guid.NewGuid().ToString("N")[..8];
        var scoped = sql.Replace($"public.{Table}", $"{scratch}.{Table}");
        scoped.Should().NotBe(sql, "the file should name the table as public.{0}", Table);

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(ct);
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}", ct);
            await ExecuteAsync(connection, $"CREATE TABLE {scratch}.{Table} (LIKE public.{Table} INCLUDING DEFAULTS)", ct);
            await ExecuteAsync(connection, scoped, ct);
            await ExecuteAsync(connection, scoped, ct);

            var expected = await IndexAsync(connection, "public", ct);
            var actual = await IndexAsync(connection, scratch, ct);

            expected.Should().NotBeNull("the module declares the (WorkflowDefinitionId, UpdatedAt, Id) index under {0}", Index);
            actual.Should().Be(expected, "the file has to create the index Marten creates, or db-assert reports it");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE", CancellationToken.None);
        }
    }

    private static async Task<string?> IndexAsync(NpgsqlConnection connection, string schema, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "select indexdef from pg_indexes where schemaname = @schema and tablename = @table and indexname = @index", connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", Table);
        command.Parameters.AddWithValue("index", Index);
        var def = (string?)await command.ExecuteScalarAsync(ct);
        return def?.Replace($"{schema}.{Table}", Table);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test binary should sit under the repository");
        return directory!.FullName;
    }
}
