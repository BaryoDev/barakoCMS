using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>
/// Each hand-applied ExternalAuth migration has to build the table Marten builds.
/// </summary>
/// <remarks>
/// Production runs <c>AutoCreate.CreateOnly</c>, and the deploy gate runs db-assert before the new
/// container starts, so an upgraded database needs the module's table files applied first. This
/// runs each file into an empty schema and compares the columns, constraints and indexes it
/// produces with the ones Marten created in <c>public</c> for the ExternalAuth module, so a file
/// cannot drift from what the module declares.
/// </remarks>
[Collection("Sequential")]
public class ExternalAuthMigrationTests
{
    private readonly IntegrationTestFixture _factory;

    public ExternalAuthMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    [Theory]
    [InlineData("4.6.0", "external-auth-identities", "mt_doc_external_identities", typeof(ExternalIdentity))]
    [InlineData("4.8.0", "external-auth-used-nonces", "mt_doc_oidc_used_nonces", typeof(OidcUsedNonce))]
    public async Task The_hand_applied_migration_builds_the_table_Marten_builds(
        string version, string file, string table, Type document)
    {
        var ct = TestContext.Current.CancellationToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            await store.Storage.Database.EnsureStorageExistsAsync(document, ct);
        }

        var sql = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", version, file + ".sql"), ct);

        var scratch = "external_auth_check_" + Guid.NewGuid().ToString("N")[..8];
        var scoped = sql.Replace($"public.{table}", $"{scratch}.{table}");
        scoped.Should().NotBe(sql, "the file should name the table as public.{0}", table);

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(ct);
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}", ct);
            await ExecuteAsync(connection, scoped, ct);
            await ExecuteAsync(connection, scoped, ct);

            var expected = await ShapeAsync(connection, "public", table, ct);
            var actual = await ShapeAsync(connection, scratch, table, ct);

            expected.Should().NotBeEmpty("the fixture registers ExternalAuth, so Marten built {0}", table);
            expected.Should().Contain(line => line.StartsWith("constraint ") && line.Contains("PRIMARY KEY (id)"),
                "the id is a hash, and the primary key is the only uniqueness rule");
            actual.Should().Equal(expected,
                "the migration has to produce exactly what the module declares, or db-assert reports it");
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
