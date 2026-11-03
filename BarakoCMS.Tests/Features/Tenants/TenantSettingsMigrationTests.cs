using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// The hand-applied tenant settings migration has to build the table Marten builds.
/// </summary>
/// <remarks>
/// The file is run into a scratch schema, so the shared tables are not touched. Run twice, it changes nothing.
/// </remarks>
[Collection("Integration1")]
public class TenantSettingsMigrationTests
{
    private const string Table = "mt_doc_tenant_settings";

    private readonly IntegrationTestFixture _factory;

    public TenantSettingsMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_hand_applied_migration_builds_the_table_Marten_builds()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            await store.Storage.Database.EnsureStorageExistsAsync(typeof(TenantSettings), Ct);
        }

        await using var connection = await OpenAsync();
        var scratch = await NewSchemaAsync(connection);
        try
        {
            var scoped = Scoped(scratch);
            await ExecuteAsync(connection, scoped);
            await ExecuteAsync(connection, scoped);

            var expected = await ShapeAsync(connection, "public", Table);
            var actual = await ShapeAsync(connection, scratch, Table);

            expected.Should().Contain(line => line.StartsWith("constraint ") && line.Contains("PRIMARY KEY (id)"));
            actual.Should().Equal(expected, "the migration has to produce exactly what Marten declares, or db-assert reports it");
        }
        finally
        {
            await DropAsync(connection, scratch);
        }
    }

    private static string Scoped(string scratch)
    {
        var sql = File.ReadAllText(Path.Combine(RepositoryRoot(), "migrations", "5.5.0", "896-tenant-settings.sql"));
        var scoped = sql.Replace("public.", scratch + ".");
        scoped.Should().NotBe(sql, "the file should name its tables as public.<table>");
        return scoped;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<string> NewSchemaAsync(NpgsqlConnection connection)
    {
        var scratch = "tenant_settings_check_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}");
        return scratch;
    }

    private static Task DropAsync(NpgsqlConnection connection, string scratch) =>
        ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE", CancellationToken.None);

    /// <summary>Columns, constraints and indexes of the table, with the schema name taken out.</summary>
    private static async Task<List<string>> ShapeAsync(NpgsqlConnection connection, string schema, string table)
    {
        var lines = new List<string>();
        await ReadAsync(connection,
            "select 'column ' || column_name || ' ' || data_type || ' ' || is_nullable || ' ' || coalesce(column_default, '') "
          + "from information_schema.columns where table_schema = @schema and table_name = @table order by column_name",
            schema, table, lines);
        await ReadAsync(connection,
            "select 'constraint ' || c.conname || ' ' || pg_get_constraintdef(c.oid) "
          + "from pg_constraint c join pg_class t on t.oid = c.conrelid join pg_namespace n on n.oid = t.relnamespace "
          + "where n.nspname = @schema and t.relname = @table order by c.conname",
            schema, table, lines);
        await ReadAsync(connection,
            "select 'index ' || indexdef from pg_indexes where schemaname = @schema and tablename = @table order by indexname",
            schema, table, lines);
        return lines.Select(line => line.Replace($"{schema}.{table}", table)).ToList();
    }

    private static async Task ReadAsync(NpgsqlConnection connection, string sql, string schema, string table, List<string> into)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            into.Add(reader.GetString(0));
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken? ct = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct ?? Ct);
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
