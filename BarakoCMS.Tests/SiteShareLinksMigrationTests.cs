using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The hand-applied site_share_links migration has to build the table Marten builds, and its
/// rollback has to take that table away again.
/// </summary>
/// <remarks>
/// Production runs <c>AutoCreate.CreateOnly</c>, and the deploy gate runs db-assert before the new
/// container starts, so a database that was on 4.0 or 4.1 needs
/// <c>migrations/4.2.0/site-share-links.sql</c> applied by hand. This runs that file into an empty
/// schema and compares the columns, constraints and indexes it produces with the ones Marten created
/// in <c>public</c>, so the file cannot drift from what the core declares.
/// </remarks>
[Collection("Sequential")]
public class SiteShareLinksMigrationTests
{
    private const string Table = "mt_doc_site_share_links";

    private readonly IntegrationTestFixture _factory;

    public SiteShareLinksMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task The_hand_applied_migration_builds_the_table_Marten_builds()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureMartenBuiltTheTableAsync(ct);
        var (scratch, scoped) = await ScopedAsync("site-share-links.sql", ct);

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(ct);
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}", ct);
            await ExecuteAsync(connection, scoped, ct);
            await ExecuteAsync(connection, scoped, ct);

            var expected = await ShapeAsync(connection, "public", ct);
            var actual = await ShapeAsync(connection, scratch, ct);

            // Six columns, the primary key constraint, and three indexes: the primary key's own,
            // the expiry index and the per-tenant unique key hash index.
            expected.Should().HaveCount(10, "that is what Marten builds for {0}", Table);
            expected.Should().Contain(line => line.StartsWith("index ") && line.Contains("'ExpiresAt'"),
                "the core declares .Index(x => x.ExpiresAt)");
            expected.Should().Contain(
                line => line.StartsWith("index CREATE UNIQUE") && line.Contains("'KeyHash'") && line.Contains("tenant_id"),
                "the core declares a per-tenant unique index on KeyHash");
            actual.Should().Equal(expected,
                "the migration has to produce exactly what the core declares, or db-assert reports it");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE", CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_rollback_drops_the_table_the_migration_built()
    {
        var ct = TestContext.Current.CancellationToken;
        // For public.mt_immutable_timestamptz, which the expiry index calls.
        await EnsureMartenBuiltTheTableAsync(ct);
        var (scratch, up) = await ScopedAsync("site-share-links.sql", ct);
        var (_, down) = await ScopedAsync("rollback-site-share-links.sql", ct, scratch);

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(ct);
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}", ct);
            await ExecuteAsync(connection, up, ct);
            (await ShapeAsync(connection, scratch, ct)).Should().HaveCount(10, "the migration built the table");

            await ExecuteAsync(connection, down, ct);
            await ExecuteAsync(connection, down, ct);

            (await ShapeAsync(connection, scratch, ct)).Should().BeEmpty("the rollback drops the table and its indexes");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE", CancellationToken.None);
        }
    }

    private async Task EnsureMartenBuiltTheTableAsync(CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await store.Storage.Database.EnsureStorageExistsAsync(typeof(SiteShareLink), ct);
    }

    /// <summary>The file with the table moved into a scratch schema, and that schema's name.</summary>
    private static async Task<(string Scratch, string Sql)> ScopedAsync(
        string file, CancellationToken ct, string? scratch = null)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", "4.2.0", file), ct);

        scratch ??= "site_share_links_check_" + Guid.NewGuid().ToString("N")[..8];
        // Only the table moves to the scratch schema; mt_immutable_timestamptz stays in public,
        // where Marten created it and where the file names it.
        var scoped = sql.Replace($"public.{Table}", $"{scratch}.{Table}");
        scoped.Should().NotBe(sql, "{0} should name the table as public.{1}", file, Table);
        return (scratch, scoped);
    }

    /// <summary>Columns, constraints and indexes of the table, with the schema name taken out.</summary>
    private static async Task<List<string>> ShapeAsync(NpgsqlConnection connection, string schema, CancellationToken ct)
    {
        var lines = new List<string>();
        await ReadAsync(connection,
            "select 'column ' || column_name || ' ' || data_type || ' ' || is_nullable || ' ' || coalesce(column_default, '') "
          + "from information_schema.columns where table_schema = @schema and table_name = @table order by column_name",
            schema, lines, ct);
        await ReadAsync(connection,
            "select 'constraint ' || c.conname || ' ' || pg_get_constraintdef(c.oid) "
          + "from pg_constraint c join pg_class t on t.oid = c.conrelid join pg_namespace n on n.oid = t.relnamespace "
          + "where n.nspname = @schema and t.relname = @table order by c.conname",
            schema, lines, ct);
        await ReadAsync(connection,
            "select 'index ' || indexdef from pg_indexes where schemaname = @schema and tablename = @table order by indexname",
            schema, lines, ct);
        return lines.Select(line => line.Replace($"{schema}.{Table}", Table)).ToList();
    }

    private static async Task ReadAsync(
        NpgsqlConnection connection, string sql, string schema, List<string> into, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", Table);
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
