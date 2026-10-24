using System.Text.RegularExpressions;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Two content types whose names differ only by case cannot both be stored, because every lookup
/// compares ignoring case and would find both (#1158).
/// </summary>
[Collection("Integration2")]
public class ContentTypeNameIndexTests
{
    private const string Table = "mt_doc_contenttypedefinition";
    private const string Index = "mt_doc_contenttypedefinition_uidx_name";
    private const string File1158 = "1158-content-type-name-index-ignore-case.sql";

    private readonly IntegrationTestFixture _factory;

    public ContentTypeNameIndexTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_name_that_differs_only_by_case_is_refused()
    {
        var name = "casing-" + Guid.NewGuid().ToString("N")[..10];

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition { Id = Guid.NewGuid(), Name = name });
            await session.SaveChangesAsync(Ct);
        }

        using var second = _factory.Services.CreateScope();
        var write = second.ServiceProvider.GetRequiredService<IDocumentSession>();
        write.Store(new ContentTypeDefinition { Id = Guid.NewGuid(), Name = name.ToUpperInvariant() });

        var save = async () => await write.SaveChangesAsync(Ct);

        await save.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task The_migration_builds_the_same_index_Marten_does()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            await store.Storage.Database.EnsureStorageExistsAsync(typeof(ContentTypeDefinition), Ct);
        }

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        string live;
        await using (var command = new NpgsqlCommand(
            "select indexdef from pg_indexes where schemaname = 'public' and tablename = @t and indexname = @i", connection))
        {
            command.Parameters.AddWithValue("t", Table);
            command.Parameters.AddWithValue("i", Index);
            live = (string)(await command.ExecuteScalarAsync(Ct))!;
        }

        live.Should().Contain("lower(");
        Normal(Expression(live)).Should().Be(Normal(Expression(await MigrationAsync())));
    }

    [Fact]
    public async Task The_migration_refuses_when_two_names_differ_only_by_case_and_changes_nothing()
    {
        var scratch = "ctname_check_" + Guid.NewGuid().ToString("N")[..8];
        var sql = (await MigrationAsync())
            .Replace($"public.{Table}", $"{scratch}.{Table}")
            .Replace("schemaname = 'public'", $"schemaname = '{scratch}'")
            .Replace("n.nspname = 'public'", $"n.nspname = '{scratch}'");

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        try
        {
            await Exec(connection,
                $"CREATE SCHEMA {scratch}; CREATE TABLE {scratch}.{Table} (id uuid, tenant_id varchar, data jsonb);" +
                $"CREATE UNIQUE INDEX {Index} ON {scratch}.{Table} USING btree (((data ->> 'Name'::text)), tenant_id);" +
                $"INSERT INTO {scratch}.{Table} VALUES (gen_random_uuid(), 'a', '{{\"Name\":\"Article\"}}'), (gen_random_uuid(), 'a', '{{\"Name\":\"article\"}}');");

            var run = async () => await Exec(connection, sql);
            (await run.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("1 name(s)");
            (await Defs(connection, scratch)).Should().ContainSingle().Which.Should().NotContain("lower(");

            await Exec(connection, $"DELETE FROM {scratch}.{Table} WHERE data ->> 'Name' = 'Article'");
            await Exec(connection, sql);
            await Exec(connection, sql);

            (await Defs(connection, scratch)).Should().ContainSingle().Which.Should().Contain("lower(").And.Contain("UNIQUE");
            var clash = async () => await Exec(connection,
                $"INSERT INTO {scratch}.{Table} VALUES (gen_random_uuid(), 'a', '{{\"Name\":\"ARTICLE\"}}')");
            (await clash.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
            await Exec(connection, $"INSERT INTO {scratch}.{Table} VALUES (gen_random_uuid(), 'b', '{{\"Name\":\"ARTICLE\"}}')");
        }
        finally
        {
            await Exec(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    private static async Task<List<string>> Defs(NpgsqlConnection connection, string schema)
    {
        var found = new List<string>();
        await using var command = new NpgsqlCommand(
            "select indexdef from pg_indexes where schemaname = @s and tablename = @t", connection);
        command.Parameters.AddWithValue("s", schema);
        command.Parameters.AddWithValue("t", Table);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            found.Add(reader.GetString(0));
        return found;
    }

    private static async Task Exec(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string Expression(string sql)
    {
        var match = Regex.Match(sql,
            @"CREATE\s+UNIQUE\s+INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?" + Index + @"\s+ON\s+\S+\s+USING\s+btree\s*(\(.*\))\s*;?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        match.Success.Should().BeTrue($"the statement should create {Index}: {sql}");
        return match.Groups[1].Value;
    }

    private static string Normal(string expression) => Regex.Replace(expression, @"\s+", " ").Trim();

    private static async Task<string> MigrationAsync() =>
        await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", "5.1.0", File1158), Ct);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
            directory = directory.Parent;
        return directory!.FullName;
    }
}
