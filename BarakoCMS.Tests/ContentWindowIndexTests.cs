using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A rollup window on createdAt reads one tenant's entries of one type in a time range. The index
/// that serves it is declared in the mapping and shipped as a migration file for a database that
/// already has the table, and the aggregate's bounds have to bind as the type the index holds.
/// </summary>
/// <remarks>
/// The file is run against a copy of the table in a scratch schema, and the index Postgres reports
/// for it, name included, is compared with the one it reports for the table Marten built.
/// </remarks>
[Collection("Integration1")]
public class ContentWindowIndexTests
{
    private const string Table = "mt_doc_contents";
    private const string Index = "mt_doc_contents_idx_content_type_created_at_tenant";
    private const string Up = "content-type-created-at-tenant-index.sql";
    private const string Down = "rollback-content-type-created-at-tenant-index.sql";

    private readonly IntegrationTestFixture _factory;

    public ContentWindowIndexTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Contents_have_an_index_on_the_lowercased_type_the_creation_time_and_the_tenant()
    {
        await EnsureTableAsync();
        await using var connection = await OpenAsync();

        var live = await IndexesAsync(connection, "public", Index);

        live.Should().HaveCount(1);
        live[0].Should().Contain("lower(").And.Contain("ContentType").And.Contain("mt_immutable_timestamp").And.Contain("CreatedAt");
        live[0].Should().EndWith("tenant_id)", "the tenant comes last, as Marten orders a per tenant index");
    }

    [Fact]
    public async Task The_migration_builds_the_index_Marten_creates()
    {
        await EnsureTableAsync();
        var scratch = NewScratchName();
        var sql = Scoped(await MigrationAsync(Up), scratch);

        await using var connection = await OpenAsync();
        try
        {
            var live = await IndexesAsync(connection, "public", Index);
            live.Should().HaveCount(1, "core declares this index under this name");

            await PrepareAsync(connection, scratch);
            await ExecuteAsync(connection, sql);

            var built = await IndexesAsync(connection, scratch, Index);
            built.Should().HaveCount(1, "the file should create the index under the name Marten gives it");
            built[0].Replace($"{scratch}.", "public.").Should().Be(live[0]);
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
        var upSql = Scoped(await MigrationAsync(Up), scratch);
        var down = await MigrationAsync(Down);
        down.Should().Contain($"DROP INDEX IF EXISTS public.{Index};");
        var downSql = down.Replace("DROP INDEX IF EXISTS public.", $"DROP INDEX IF EXISTS {scratch}.");

        await using var connection = await OpenAsync();
        try
        {
            await PrepareAsync(connection, scratch);

            await ExecuteAsync(connection, upSql);
            await ExecuteAsync(connection, upSql);
            (await IndexesAsync(connection, scratch, Index)).Should().HaveCount(1);

            await ExecuteAsync(connection, downSql);
            await ExecuteAsync(connection, downSql);
            (await IndexesAsync(connection, scratch, Index)).Should().BeEmpty();
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    [Fact]
    public async Task A_createdat_window_reads_through_the_index_with_bounds_that_bind_as_timestamp()
    {
        await EnsureTableAsync();
        var scratch = NewScratchName();
        var sql = Scoped(await MigrationAsync(Up), scratch);

        await using var connection = await OpenAsync();
        try
        {
            await PrepareAsync(connection, scratch);
            await ExecuteAsync(connection, sql);

            // 60,000 entries over 24 months, 3 tenants and 4 types: a month of one type in one tenant
            // is a sliver of the table, so the planner has a reason to prefer the index.
            await ExecuteAsync(connection,
                $"INSERT INTO {scratch}.{Table} (id, data, tenant_id) "
                + "SELECT md5('window-' || n)::uuid, jsonb_build_object("
                + "'ContentType', 'Line' || (n % 4), "
                + "'CreatedAt', to_char(timestamp '2025-01-01' + (n % 730) * interval '1 day' + (n % 86400) * interval '1 second', 'YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"')), "
                + "'tenant-' || (n % 3) FROM generate_series(1, 60000) AS n");
            await ExecuteAsync(connection, $"ANALYZE {scratch}.{Table}");

            var plan = new List<string>();
            await using (var command = new NpgsqlCommand(
                $"EXPLAIN select count(*) from {scratch}.{Table} d "
                + "where d.tenant_id = @tenant and lower(d.data ->> 'ContentType') = @type "
                + $"and public.mt_immutable_timestamp(d.data ->> 'CreatedAt') >= @from::timestamp "
                + $"and public.mt_immutable_timestamp(d.data ->> 'CreatedAt') < @to::timestamp",
                connection))
            {
                command.Parameters.AddWithValue("tenant", "tenant-1");
                command.Parameters.AddWithValue("type", "line2");
                command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Timestamp) { Value = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Unspecified) });
                command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Timestamp) { Value = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Unspecified) });
                await using var reader = await command.ExecuteReaderAsync(Ct);
                while (await reader.ReadAsync(Ct))
                    plan.Add(reader.GetString(0));
            }

            var text = string.Join("\n", plan);
            text.Should().Contain(Index, "the window should be read through the index");
            text.Should().NotContain($"Seq Scan on {Table}");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    private static string NewScratchName() => "window_index_" + Guid.NewGuid().ToString("N")[..8];

    private static async Task PrepareAsync(NpgsqlConnection connection, string scratch) =>
        await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}; CREATE TABLE {scratch}.{Table} (LIKE public.{Table} INCLUDING DEFAULTS);");

    private async Task EnsureTableAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await store.Storage.Database.EnsureStorageExistsAsync(typeof(Content), Ct);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<List<string>> IndexesAsync(NpgsqlConnection connection, string schema, string index)
    {
        var found = new List<string>();
        await using var command = new NpgsqlCommand(
            "select indexdef from pg_indexes where schemaname = @schema and tablename = @table and indexname = @index",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", Table);
        command.Parameters.AddWithValue("index", index);
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

    /// <summary>The file with the table moved into a scratch schema.</summary>
    private static string Scoped(string sql, string scratch)
    {
        var scoped = sql.Replace($"public.{Table}", $"{scratch}.{Table}");
        scoped.Should().NotBe(sql, "the file should name the schema it works in as public");
        return scoped;
    }

    private static async Task<string> MigrationAsync(string file) =>
        await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "migrations", "6.0.0", file), Ct);
}
