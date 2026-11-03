using barakoCMS.Features.Workflows;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The hand-applied workflow-missed-triggers migration has to build the table Marten builds, under
/// the name the document is mapped to.
/// </summary>
[Collection("Integration1")]
public class WorkflowMissedTriggerMigrationTests
{
    private const string Table = "mt_doc_workflow_missed_triggers";

    private readonly IntegrationTestFixture _factory;

    public WorkflowMissedTriggerMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task The_hand_applied_migration_builds_the_table_Marten_builds()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            await store.Storage.Database.EnsureStorageExistsAsync(typeof(MissedWorkflowTrigger), ct);
        }

        var sql = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "migrations", "5.5.0", "workflow-missed-triggers.sql"), ct);

        var scratch = "missed_trigger_check_" + Guid.NewGuid().ToString("N")[..8];
        var scoped = sql.Replace($"public.{Table}", $"{scratch}.{Table}");
        scoped.Should().NotBe(sql, "the file should name the table as public.{0}", Table);

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(ct);
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}", ct);
            await ExecuteAsync(connection, scoped, ct);
            await ExecuteAsync(connection, scoped, ct);

            var expected = await ShapeAsync(connection, "public", Table, ct);
            var actual = await ShapeAsync(connection, scratch, Table, ct);

            expected.Should().NotBeEmpty("Marten built {0} for MissedWorkflowTrigger under its alias", Table);
            expected.Should().Contain(line => line.StartsWith("column tenant_id "),
                "the document is multi-tenanted, so the table carries the tenant column");
            expected.Should().Contain(line => line.StartsWith("constraint ") && line.Contains("PRIMARY KEY (tenant_id, id)"),
                "a conjoined table keys on the tenant and the id");
            actual.Should().Equal(expected,
                "the migration has to produce exactly what Marten declares, or db-assert reports it");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE", CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_rollback_removes_the_table_the_migration_builds()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = RepositoryRoot();
        var migration = await File.ReadAllTextAsync(Path.Combine(root, "migrations", "5.5.0", "workflow-missed-triggers.sql"), ct);
        var rollback = await File.ReadAllTextAsync(Path.Combine(root, "migrations", "5.5.0", "rollback-workflow-missed-triggers.sql"), ct);

        var scratch = "missed_trigger_rollback_" + Guid.NewGuid().ToString("N")[..8];
        var scopedMigration = migration.Replace($"public.{Table}", $"{scratch}.{Table}");
        var scopedRollback = rollback.Replace($"public.{Table}", $"{scratch}.{Table}");
        scopedRollback.Should().NotBe(rollback, "the rollback should name the table as public.{0}", Table);

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(ct);
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}", ct);
            await ExecuteAsync(connection, scopedMigration, ct);
            (await ShapeAsync(connection, scratch, Table, ct)).Should().NotBeEmpty("the migration builds the table first");

            await ExecuteAsync(connection, scopedRollback, ct);
            await ExecuteAsync(connection, scopedRollback, ct);

            (await ShapeAsync(connection, scratch, Table, ct)).Should().BeEmpty("the rollback drops the table, twice without error");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE", CancellationToken.None);
        }
    }

    /// <summary>Columns, constraints and indexes of the table, with the schema name taken out.</summary>
    private static async Task<List<string>> ShapeAsync(NpgsqlConnection connection, string schema, string table, CancellationToken ct)
    {
        var lines = new List<string>();
        await ReadAsync(connection,
            "select 'column ' || column_name || ' ' || data_type || ' ' || is_nullable || ' ' || coalesce(column_default, '') "
          + "from information_schema.columns where table_schema = @schema and table_name = @table order by column_name",
            schema, table, lines, ct);
        await ReadAsync(connection,
            "select 'constraint ' || c.conname || ' ' || pg_get_constraintdef(c.oid) "
          + "from pg_constraint c join pg_class t on t.oid = c.conrelid join pg_namespace n on n.oid = t.relnamespace "
          + "where n.nspname = @schema and t.relname = @table order by c.conname",
            schema, table, lines, ct);
        await ReadAsync(connection,
            "select 'index ' || indexdef from pg_indexes where schemaname = @schema and tablename = @table order by indexname",
            schema, table, lines, ct);
        return lines.Select(line => line.Replace($"{schema}.{table}", table)).ToList();
    }

    private static async Task ReadAsync(
        NpgsqlConnection connection, string sql, string schema, string table, List<string> into, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            into.Add(reader.GetString(0));
        }
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
