using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The content type uniqueness check and the idempotency sweep filter on columns that had no index
/// that could serve them. Each index is declared in the mapping and shipped as a migration file for
/// a database that already has the table.
/// </summary>
/// <remarks>
/// Each file is run against a copy of the table in a scratch schema, and the index Postgres reports
/// for it, name included, is compared with the one it reports for the table Marten built.
/// </remarks>
[Collection("Integration1")]
public class LookupIndexTests
{
    private const string ContentsTable = "mt_doc_contents";
    private const string ContentsIndex = "mt_doc_contents_idx_content_type_lower";
    private const string RawContentsIndex = "mt_doc_contents_idx_content_type";
    private const string IdempotencyTable = "mt_doc_idempotency_records";
    private const string IdempotencyIndex = "mt_doc_idempotency_records_idx_created_at";

    private readonly IntegrationTestFixture _factory;

    public LookupIndexTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Contents_have_an_index_on_the_lowercased_type_next_to_the_raw_one()
    {
        await EnsureTableAsync(ContentsTable);
        await using var connection = await OpenAsync();

        var lowered = await IndexesAsync(connection, "public", ContentsTable, ContentsIndex);
        lowered.Should().HaveCount(1);
        lowered[0].Should().Contain("lower(");
        lowered[0].Should().Contain("ContentType");

        (await IndexesAsync(connection, "public", ContentsTable, RawContentsIndex)).Should().HaveCount(1,
            "adding the lowercased index must not replace the plain one");
    }

    [Fact]
    public async Task Idempotency_records_have_an_index_on_the_creation_time()
    {
        await EnsureTableAsync(IdempotencyTable);
        await using var connection = await OpenAsync();

        var live = await IndexesAsync(connection, "public", IdempotencyTable, IdempotencyIndex);

        live.Should().HaveCount(1);
        live[0].Should().Contain("CreatedAt");
    }

    [Theory]
    [InlineData("content-type-lower-index.sql", "rollback-content-type-lower-index.sql", ContentsTable, ContentsIndex)]
    [InlineData("idempotency-created-at-index.sql", "rollback-idempotency-created-at-index.sql", IdempotencyTable, IdempotencyIndex)]
    public async Task The_migration_builds_the_index_Marten_creates(string up, string down, string table, string index)
    {
        await EnsureTableAsync(table);
        var scratch = NewScratchName();
        var sql = Scoped(await MigrationAsync(up), table, scratch);

        await using var connection = await OpenAsync();
        try
        {
            var live = await IndexesAsync(connection, "public", table, index);
            live.Should().HaveCount(1, "core declares this index under this name");

            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}; CREATE TABLE {scratch}.{table} (LIKE public.{table} INCLUDING DEFAULTS);");
            await ExecuteAsync(connection, sql);

            var built = await IndexesAsync(connection, scratch, table, index);
            built.Should().HaveCount(1, "the file should create the index under the name Marten gives it");
            built[0].Replace($"{scratch}.", "public.").Should().Be(live[0]);
            (await MigrationAsync(down)).Should().Contain($"DROP INDEX IF EXISTS public.{index};");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    [Theory]
    [InlineData("content-type-lower-index.sql", "rollback-content-type-lower-index.sql", ContentsTable, ContentsIndex)]
    [InlineData("idempotency-created-at-index.sql", "rollback-idempotency-created-at-index.sql", IdempotencyTable, IdempotencyIndex)]
    public async Task The_migration_can_run_twice_and_the_rollback_drops_the_index(string up, string down, string table, string index)
    {
        await EnsureTableAsync(table);
        var scratch = NewScratchName();
        var upSql = Scoped(await MigrationAsync(up), table, scratch);
        var downSql = Scoped(await MigrationAsync(down), table, scratch, indexSchema: true);

        await using var connection = await OpenAsync();
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}; CREATE TABLE {scratch}.{table} (LIKE public.{table} INCLUDING DEFAULTS);");

            await ExecuteAsync(connection, upSql);
            await ExecuteAsync(connection, upSql);
            (await IndexesAsync(connection, scratch, table, index)).Should().HaveCount(1);

            await ExecuteAsync(connection, downSql);
            await ExecuteAsync(connection, downSql);
            (await IndexesAsync(connection, scratch, table, index)).Should().BeEmpty();
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    private static string NewScratchName() => "lookup_index_" + Guid.NewGuid().ToString("N")[..8];

    private async Task EnsureTableAsync(string table)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var document = table == ContentsTable ? typeof(Content) : typeof(IdempotencyRecord);
        await store.Storage.Database.EnsureStorageExistsAsync(document, Ct);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<List<string>> IndexesAsync(NpgsqlConnection connection, string schema, string table, string index)
    {
        var found = new List<string>();
        await using var command = new NpgsqlCommand(
            "select indexdef from pg_indexes where schemaname = @schema and tablename = @table and indexname = @index",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
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

    /// <summary>
    /// The file with the table moved into a scratch schema. A rollback names only the index, so it is
    /// moved by the index name.
    /// </summary>
    private static string Scoped(string sql, string table, string scratch, bool indexSchema = false)
    {
        var scoped = indexSchema
            ? sql.Replace("DROP INDEX IF EXISTS public.", $"DROP INDEX IF EXISTS {scratch}.")
            : sql.Replace($"public.{table}", $"{scratch}.{table}");
        scoped.Should().NotBe(sql, "the file should name the schema it works in as public");
        return scoped;
    }

    private static async Task<string> MigrationAsync(string file) =>
        await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", "5.5.0", file), Ct);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test binary should sit under the repository");
        return directory!.FullName;
    }
}
