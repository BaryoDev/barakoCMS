using barakoCMS.Events;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The hand-applied event headers migration has to leave what Marten builds, and its rollback
/// has to put back what was there.
/// </summary>
/// <remarks>
/// Production runs <c>AutoCreate.CreateOnly</c>, so an upgraded database gets <c>mt_events.headers</c>
/// and the new <c>mt_quick_append_events</c> only from <c>migrations/5.1.0/event-headers.sql</c>.
/// Marten compares the function as text, so a body that differs by one space is a schema the host
/// refuses to start on.
///
/// A scratch schema is put in the state a 5.0 database is in (the events table without the headers
/// column, and the function <c>migrations/5.0.0</c> installs), the file runs into it twice, and the
/// result is compared with <c>public</c>, which Marten built for this run with the same options
/// production uses for the event store.
/// </remarks>
[Collection("Integration4")]
public class EventHeadersMigrationTests
{
    private const string Function = "mt_quick_append_events";

    private readonly IntegrationTestFixture _factory;

    public EventHeadersMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task The_hand_applied_migration_leaves_the_column_and_the_function_Marten_builds()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureMartenBuiltTheEventStoreAsync(ct);
        var scratch = NewScratchName();

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(ct);
        try
        {
            await CreateAsOf50Async(connection, scratch, ct);
            (await HeadersColumnAsync(connection, scratch, ct)).Should().BeEmpty(
                "the scratch table starts as a 5.0 one, or the comparison below proves nothing");

            var up = await ScopedAsync("5.1.0", "event-headers.sql", scratch, ct);
            await ExecuteAsync(connection, up, ct);
            await ExecuteAsync(connection, up, ct);

            var expectedColumn = await HeadersColumnAsync(connection, "public", ct);
            expectedColumn.Should().ContainSingle("the core turns on event headers")
                .Which.Should().StartWith("headers jsonb");
            (await HeadersColumnAsync(connection, scratch, ct)).Should().Equal(expectedColumn,
                "a column of the right name and the wrong type is one db-assert asks to change");

            var expectedFunction = await FunctionsAsync(connection, "public", ct);
            expectedFunction.Should().HaveCount(1, "Marten declares one {0}", Function);
            expectedFunction[0].Should().Contain("headers jsonb[]",
                "public is the store with headers on, or this compares the wrong thing");

            var actualFunction = await FunctionsAsync(connection, scratch, ct);
            actualFunction.Should().HaveCount(1,
                "the file drops the function under the old argument list, and leaving it would be a second overload");
            actualFunction[0].Replace(scratch + ".", "public.").Should().Be(expectedFunction[0],
                "Marten compares the function as text, so the file has to produce it exactly");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE", CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_rollback_puts_back_the_table_and_the_function_5_0_declares()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureMartenBuiltTheEventStoreAsync(ct);
        var scratch = NewScratchName();

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(ct);
        try
        {
            await CreateAsOf50Async(connection, scratch, ct);
            var before = await FunctionsAsync(connection, scratch, ct);
            before.Should().HaveCount(1);
            before[0].Should().NotContain("headers jsonb[]", "the scratch schema is named for headers, so the argument is what is looked for");

            await ExecuteAsync(connection, await ScopedAsync("5.1.0", "event-headers.sql", scratch, ct), ct);
            (await HeadersColumnAsync(connection, scratch, ct)).Should().ContainSingle("the migration ran");

            var down = await ScopedAsync("5.1.0", "rollback-event-headers.sql", scratch, ct);
            await ExecuteAsync(connection, down, ct);
            await ExecuteAsync(connection, down, ct);

            (await HeadersColumnAsync(connection, scratch, ct)).Should().BeEmpty(
                "a 5.0 build reports a column it does not declare as one to drop");
            (await FunctionsAsync(connection, scratch, ct)).Should().Equal(before,
                "a 5.0 build calls the function with the argument list it had, and asserts its text");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE", CancellationToken.None);
        }
    }

    private static string NewScratchName() => "event_headers_check_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Appending one event is what makes Marten build the event store in <c>public</c>.</summary>
    private async Task EnsureMartenBuiltTheEventStoreAsync(CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Events.StartStream<barakoCMS.Models.Content>(id, new ContentCreated(
            id, "event-headers-migration-probe", new Dictionary<string, object> { ["Title"] = "probe" },
            barakoCMS.Models.ContentStatus.Draft, Guid.NewGuid(), null,
            barakoCMS.Models.SensitivityLevel.Public, DateTime.UtcNow));
        await session.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The events table as 5.0 has it, and the function 5.0 declares, in a schema of their own.
    /// </summary>
    private static async Task CreateAsOf50Async(NpgsqlConnection connection, string scratch, CancellationToken ct)
    {
        await ExecuteAsync(connection,
            $"CREATE SCHEMA {scratch}; "
          + $"CREATE TABLE {scratch}.mt_events (LIKE public.mt_events INCLUDING DEFAULTS); "
          + $"ALTER TABLE {scratch}.mt_events DROP COLUMN headers;",
            ct);
        await ExecuteAsync(connection, await ScopedAsync("5.0.0", "marten-9-49-quick-append-events.sql", scratch, ct), ct);
    }

    /// <summary>A migration file with every object moved from <c>public</c> into the scratch schema.</summary>
    private static async Task<string> ScopedAsync(string version, string file, string scratch, CancellationToken ct)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", version, file), ct);
        var scoped = sql.Replace("public.", scratch + ".");
        scoped.Should().NotBe(sql, "{0} should name its objects as public.", file);
        return scoped;
    }

    /// <summary>The headers column of <c>mt_events</c> in a schema: name, type, nullability, default.</summary>
    private static Task<List<string>> HeadersColumnAsync(NpgsqlConnection connection, string schema, CancellationToken ct) =>
        ReadAsync(connection,
            "select column_name || ' ' || data_type "
          + "|| ' nullable=' || is_nullable || ' default=' || coalesce(column_default, 'none') "
          + "from information_schema.columns where table_schema = @schema and table_name = 'mt_events' "
          + "and column_name = 'headers'",
            schema, ct);

    /// <summary>Every function of that name in a schema, as PostgreSQL writes it back.</summary>
    private static Task<List<string>> FunctionsAsync(NpgsqlConnection connection, string schema, CancellationToken ct) =>
        ReadAsync(connection,
            "select pg_get_functiondef(p.oid) from pg_proc p join pg_namespace n on n.oid = p.pronamespace "
          + $"where n.nspname = @schema and p.proname = '{Function}' order by p.oid",
            schema, ct);

    private static async Task<List<string>> ReadAsync(
        NpgsqlConnection connection, string sql, string schema, CancellationToken ct)
    {
        var lines = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
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
