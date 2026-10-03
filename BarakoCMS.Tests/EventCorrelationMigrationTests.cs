using barakoCMS.Events;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The hand-applied event correlation migration has to leave what Marten builds, and its rollback
/// has to put back what was there.
/// </summary>
/// <remarks>
/// Production runs <c>AutoCreate.CreateOnly</c>, so an upgraded database gets the two columns on
/// <c>mt_events</c> and the new <c>mt_quick_append_events</c> only from
/// <c>migrations/4.6.0/event-correlation-metadata.sql</c>. The function is the part that can go
/// wrong quietly: Marten compares its text, so a body that differs by one space is a schema the
/// host refuses to start on.
///
/// A scratch schema is put in the state a 4.5 database is in (the events table without the two
/// columns, and the function <c>migrations/4.4.0</c> installs), the file runs into it twice, and
/// the result is compared with <c>public</c>, which Marten built for this run with the same
/// options production uses for the event store.
/// </remarks>
[Collection("Sequential")]
public class EventCorrelationMigrationTests
{
    private const string Function = "mt_quick_append_events";

    private readonly IntegrationTestFixture _factory;

    public EventCorrelationMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task The_hand_applied_migration_leaves_the_columns_and_the_function_Marten_builds()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureMartenBuiltTheEventStoreAsync(ct);
        var scratch = NewScratchName();

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(ct);
        try
        {
            await CreateAsOf45Async(connection, scratch, ct);
            (await MetadataColumnsAsync(connection, scratch, ct)).Should().BeEmpty(
                "the scratch table starts as a 4.5 one, or the comparison below proves nothing");

            var up = await ScopedAsync("4.6.0", "event-correlation-metadata.sql", scratch, ct);
            await ExecuteAsync(connection, up, ct);
            await ExecuteAsync(connection, up, ct);

            var expectedColumns = await MetadataColumnsAsync(connection, "public", ct);
            expectedColumns.Should().HaveCount(2, "the core turns on the correlation id and the causation id");
            (await MetadataColumnsAsync(connection, scratch, ct)).Should().Equal(expectedColumns,
                "a column of the right name and the wrong type is one db-assert asks to change");

            var expectedFunction = await FunctionsAsync(connection, "public", ct);
            expectedFunction.Should().HaveCount(1, "Marten declares one {0}", Function);
            expectedFunction[0].Should().Contain("correlation_ids",
                "public is the store with the metadata on, or this compares the wrong thing");

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
    public async Task The_rollback_puts_back_the_table_and_the_function_a_4_5_build_declares()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureMartenBuiltTheEventStoreAsync(ct);
        var scratch = NewScratchName();

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(ct);
        try
        {
            await CreateAsOf45Async(connection, scratch, ct);
            var before = await FunctionsAsync(connection, scratch, ct);
            before.Should().HaveCount(1);
            before[0].Should().NotContain("correlation_ids");

            await ExecuteAsync(connection, await ScopedAsync("4.6.0", "event-correlation-metadata.sql", scratch, ct), ct);
            (await MetadataColumnsAsync(connection, scratch, ct)).Should().HaveCount(2, "the migration ran");

            var down = await ScopedAsync("4.6.0", "rollback-event-correlation-metadata.sql", scratch, ct);
            await ExecuteAsync(connection, down, ct);
            await ExecuteAsync(connection, down, ct);

            (await MetadataColumnsAsync(connection, scratch, ct)).Should().BeEmpty(
                "a 4.5 build reports a column it does not declare as one to drop");
            (await FunctionsAsync(connection, scratch, ct)).Should().Equal(before,
                "a 4.5 build calls the function with the argument list it had, and asserts its text");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE", CancellationToken.None);
        }
    }

    private static string NewScratchName() => "event_correlation_check_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Appending one event is what makes Marten build the event store in <c>public</c>.</summary>
    private async Task EnsureMartenBuiltTheEventStoreAsync(CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Events.StartStream<barakoCMS.Models.Content>(id, new ContentCreated(
            id, "event-correlation-migration-probe", new Dictionary<string, object> { ["Title"] = "probe" },
            barakoCMS.Models.ContentStatus.Draft, Guid.NewGuid(), null,
            barakoCMS.Models.SensitivityLevel.Public, DateTime.UtcNow));
        await session.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The events table as 4.5 has it, and the function 4.5 declares, in a schema of their own.
    /// </summary>
    private static async Task CreateAsOf45Async(NpgsqlConnection connection, string scratch, CancellationToken ct)
    {
        await ExecuteAsync(connection,
            $"CREATE SCHEMA {scratch}; "
          + $"CREATE TABLE {scratch}.mt_events (LIKE public.mt_events INCLUDING DEFAULTS); "
          + $"ALTER TABLE {scratch}.mt_events DROP COLUMN correlation_id, DROP COLUMN causation_id;",
            ct);
        await ExecuteAsync(connection, await ScopedAsync("4.4.0", "marten-9-38-quick-append-events.sql", scratch, ct), ct);
    }

    /// <summary>A migration file with every object moved from <c>public</c> into the scratch schema.</summary>
    private static async Task<string> ScopedAsync(string version, string file, string scratch, CancellationToken ct)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", version, file), ct);
        var scoped = sql.Replace("public.", scratch + ".");
        scoped.Should().NotBe(sql, "{0} should name its objects as public.", file);
        return scoped;
    }

    /// <summary>The two metadata columns of <c>mt_events</c> in a schema: name, type, nullability, default.</summary>
    private static Task<List<string>> MetadataColumnsAsync(NpgsqlConnection connection, string schema, CancellationToken ct) =>
        ReadAsync(connection,
            "select column_name || ' ' || data_type || ' ' || coalesce(character_maximum_length::text, 'unbounded') "
          + "|| ' nullable=' || is_nullable || ' default=' || coalesce(column_default, 'none') "
          + "from information_schema.columns where table_schema = @schema and table_name = 'mt_events' "
          + "and column_name in ('correlation_id', 'causation_id') order by column_name",
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
