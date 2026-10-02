using BarakoCMS.Forms;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.Forms;

/// <summary>
/// The hand-applied email verification migration has to build the tables Marten builds.
/// </summary>
/// <remarks>
/// Production runs <c>AutoCreate.CreateOnly</c>, and the deploy gate runs db-assert before the new
/// container starts, so an upgraded database gets <c>migrations/4.6.0/forms-email-verification.sql</c>
/// applied by hand. This runs that file into an empty schema and compares the columns, constraints
/// and indexes it produces with the ones Marten created in <c>public</c> for the Forms module, so
/// the file cannot drift from what the module declares. Same shape as
/// <see cref="SentEmailsMigrationTests"/>.
/// </remarks>
[Collection("Sequential")]
public class FormEmailVerificationMigrationTests
{
    private static readonly string[] Tables = ["mt_doc_form_email_verifications", "mt_doc_form_email_budgets"];

    private readonly IntegrationTestFixture _factory;

    public FormEmailVerificationMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task The_hand_applied_migration_builds_the_tables_Marten_builds()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            await store.Storage.Database.EnsureStorageExistsAsync(typeof(FormEmailVerification), ct);
            await store.Storage.Database.EnsureStorageExistsAsync(typeof(FormEmailBudget), ct);
        }

        var sql = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "migrations", "4.6.0", "forms-email-verification.sql"), ct);

        var scratch = "form_email_check_" + Guid.NewGuid().ToString("N")[..8];
        var scoped = sql;
        foreach (var table in Tables)
        {
            var moved = scoped.Replace($"public.{table}", $"{scratch}.{table}");
            moved.Should().NotBe(scoped, "the file should name the table as public.{0}", table);
            scoped = moved;
        }

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(ct);
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}", ct);
            await ExecuteAsync(connection, scoped, ct);
            await ExecuteAsync(connection, scoped, ct);

            foreach (var table in Tables)
            {
                var expected = await ShapeAsync(connection, "public", table, ct);
                var actual = await ShapeAsync(connection, scratch, table, ct);

                expected.Should().NotBeEmpty("the fixture registers Forms, so Marten built {0}", table);
                expected.Should().Contain(line => line.StartsWith("column tenant_id "),
                    "{0} is stored per tenant", table);
                actual.Should().Equal(expected,
                    "the migration has to produce exactly what the module declares for {0}, or db-assert reports it", table);
            }
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
