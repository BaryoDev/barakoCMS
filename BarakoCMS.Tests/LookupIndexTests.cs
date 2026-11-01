using System.Text.RegularExpressions;
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
[Collection("Integration1")]
public class LookupIndexTests
{
    private readonly IntegrationTestFixture _factory;

    public LookupIndexTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Contents_have_an_index_on_the_lowercased_type()
    {
        var live = await LiveExpressionsAsync(typeof(Content), "mt_doc_contents");

        live.Should().NotBeEmpty();
        live.Should().Contain(Normal("lower(data->>'ContentType')"));
    }

    [Fact]
    public async Task Idempotency_records_have_an_index_on_the_creation_time()
    {
        var live = await LiveExpressionsAsync(typeof(IdempotencyRecord), "mt_doc_idempotency_records");

        live.Should().NotBeEmpty();
        live.Should().Contain(Normal("mt_immutable_timestamp(data->>'CreatedAt')"));
    }

    [Fact]
    public async Task The_content_type_migration_builds_the_index_the_mapping_declares()
    {
        var file = await MigrationAsync("content-type-lower-index.sql");
        var live = await LiveExpressionsAsync(typeof(Content), "mt_doc_contents");

        live.Should().Contain(Normal(Expression(file)));
    }

    [Fact]
    public async Task The_idempotency_migration_builds_the_index_the_mapping_declares()
    {
        var file = await MigrationAsync("idempotency-created-at-index.sql");
        var live = await LiveExpressionsAsync(typeof(IdempotencyRecord), "mt_doc_idempotency_records");

        live.Should().Contain(Normal(Expression(file)));
    }

    [Theory]
    [InlineData("content-type-lower-index.sql", "rollback-content-type-lower-index.sql")]
    [InlineData("idempotency-created-at-index.sql", "rollback-idempotency-created-at-index.sql")]
    public async Task Each_rollback_drops_the_index_its_migration_creates(string up, string down)
    {
        var name = Regex.Match(await MigrationAsync(up), @"CREATE\s+INDEX\s+IF\s+NOT\s+EXISTS\s+(\w+)", RegexOptions.IgnoreCase)
            .Groups[1].Value;

        name.Should().NotBeEmpty();
        (await MigrationAsync(down)).Should().Contain($"DROP INDEX IF EXISTS public.{name};");
    }

    private async Task<List<string>> LiveExpressionsAsync(Type document, string table)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            await store.Storage.Database.EnsureStorageExistsAsync(document, Ct);
        }

        var found = new List<string>();
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select indexdef from pg_indexes where schemaname = 'public' and tablename = @t", connection);
        command.Parameters.AddWithValue("t", table);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            var match = Regex.Match(reader.GetString(0), @"USING\s+btree\s*(\(.*\))\s*$", RegexOptions.Singleline);
            if (match.Success)
                found.Add(Normal(match.Groups[1].Value));
        }

        return found;
    }

    private static string Expression(string sql)
    {
        var match = Regex.Match(sql,
            @"CREATE\s+INDEX\s+IF\s+NOT\s+EXISTS\s+\w+\s+ON\s+\S+\s+USING\s+btree\s*(\(.*\))\s*;",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        match.Success.Should().BeTrue($"the file should create an index: {sql}");
        return match.Groups[1].Value;
    }

    private static string Normal(string expression)
    {
        var text = expression.Replace("public.", "").Replace("::text", "");
        return Regex.Replace(text, @"[\s()]", "");
    }

    private static async Task<string> MigrationAsync(string file) =>
        await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", "5.5.0", file), Ct);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
            directory = directory.Parent;
        return directory!.FullName;
    }
}
