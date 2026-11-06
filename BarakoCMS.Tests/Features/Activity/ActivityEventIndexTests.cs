using barakoCMS.Events;
using barakoCMS.Features.Activity;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.Activity;

/// <summary>
/// The activity feed reads content events of one tenant by time. mt_events had no index on the
/// timestamp, so the read scanned the tenant's events. migrations/5.6.0/event-tenant-timestamp-index.sql
/// adds one, and the two query shapes the feed uses have to be ones Postgres can walk it with.
/// </summary>
/// <remarks>
/// The plan is read from the SQL Marten generates for the feed's own query shapes, on a tenant seeded
/// with events. Sequential scans are switched off for the session that explains, so a small test table
/// shows which index the planner would choose and not that a scan of a few pages is cheap.
/// </remarks>
[Collection("Integration1")]
public class ActivityEventIndexTests
{
    private const string IndexName = "mt_events_idx_tenant_timestamp";

    private readonly IntegrationTestFixture _fixture;

    public ActivityEventIndexTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    [Fact]
    public async Task The_newest_first_read_of_a_seeded_tenant_uses_the_tenant_timestamp_index()
    {
        var tenant = await SeedAsync();
        await using var connection = await PrepareAsync();

        await using var session = Store.QuerySession(tenant);
        var query = ActivitySources.NewestFirst(
            session.Events.QueryAllRawEvents(), DateTimeOffset.UtcNow - TimeSpan.FromDays(400), 50);

        var plan = await ExplainAsync(connection, query.ToCommand());

        plan.Should().Contain(IndexName, "the tenant filter, the timestamp bound and the order are what the index is on");
    }

    [Fact]
    public async Task The_read_after_an_old_cursor_uses_the_tenant_timestamp_index()
    {
        var tenant = await SeedAsync();
        await using var connection = await PrepareAsync();

        await using var session = Store.QuerySession(tenant);
        var cursor = new ActivityCursor(DateTimeOffset.UtcNow - TimeSpan.FromDays(400), ActivitySource.Events, Guid.NewGuid(), 1);
        var query = ActivitySources.OldestAfter(session.Events.QueryAllRawEvents(), cursor, 1, 50);

        var plan = await ExplainAsync(connection, query.ToCommand());

        plan.Should().Contain(IndexName);

        // Naming the index is not enough: the tenant condition alone can pick it. Postgres prints the
        // conditions it walks the index with as "Index Cond: (... AND (\"timestamp\" >= ...))", and the
        // plain range is what the `e.Timestamp >= at` conjunct adds next to the OR.
        var indexConds = plan.Split('\n').Where(l => l.Contains("Index Cond:")).ToList();
        indexConds.Should().NotBeEmpty("the plan walks the index");
        indexConds.Should().Contain(
            l => System.Text.RegularExpressions.Regex.IsMatch(l, "timestamp\"?\\s*>="),
            "the timestamp bound is part of the index condition, not left to a filter");
    }

    [Fact]
    public async Task Without_the_index_the_same_read_does_not_name_it()
    {
        var tenant = await SeedAsync();
        await using var connection = await PrepareAsync(withIndex: false);

        await using var session = Store.QuerySession(tenant);
        var query = ActivitySources.NewestFirst(
            session.Events.QueryAllRawEvents(), DateTimeOffset.UtcNow - TimeSpan.FromDays(400), 50);

        var plan = await ExplainAsync(connection, query.ToCommand());

        plan.Should().NotBeEmpty();
        plan.Should().NotContain(IndexName, "this is the control: the plan above names the index because the file built it");
    }

    [Fact]
    public async Task The_migration_can_run_twice_and_the_rollback_drops_the_index()
    {
        await using var connection = await PrepareAsync();

        await ExecuteAsync(connection, await MigrationAsync("event-tenant-timestamp-index.sql"));
        (await IndexCountAsync(connection)).Should().Be(1);

        await ExecuteAsync(connection, await MigrationAsync("rollback-event-tenant-timestamp-index.sql"));
        await ExecuteAsync(connection, await MigrationAsync("rollback-event-tenant-timestamp-index.sql"));
        (await IndexCountAsync(connection)).Should().Be(0);

        await ExecuteAsync(connection, await MigrationAsync("event-tenant-timestamp-index.sql"));
    }

    /// <summary>A tenant with a few hundred streams, and a second one so the tenant filter has something to exclude.</summary>
    private async Task<string> SeedAsync()
    {
        var tenant = "evidx-" + Guid.NewGuid().ToString("N")[..10];
        var other = "evidx-" + Guid.NewGuid().ToString("N")[..10];
        foreach (var (slug, count) in new[] { (tenant, 300), (other, 100) })
        {
            await using var session = Store.LightweightSession(slug);
            for (var i = 0; i < count; i++)
            {
                var id = Guid.NewGuid();
                session.Events.StartStream<Content>(id, new ContentCreated(
                    id, "article", new Dictionary<string, object> { ["Title"] = $"t{i}" },
                    ContentStatus.Draft, Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow));
            }

            await session.SaveChangesAsync(Ct);
        }

        return tenant;
    }

    /// <summary>An open connection with the index in the state asked for, statistics current and sequential scans off.</summary>
    private async Task<NpgsqlConnection> PrepareAsync(bool withIndex = true)
    {
        var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await ExecuteAsync(connection, await MigrationAsync("rollback-event-tenant-timestamp-index.sql"));
        if (withIndex) await ExecuteAsync(connection, await MigrationAsync("event-tenant-timestamp-index.sql"));
        await ExecuteAsync(connection, "ANALYZE public.mt_events");
        await ExecuteAsync(connection, "SET enable_seqscan = off");
        return connection;
    }

    private static async Task<string> ExplainAsync(NpgsqlConnection connection, NpgsqlCommand command)
    {
        command.Connection = connection;
        command.CommandText = "EXPLAIN " + command.CommandText;
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    private static async Task<long> IndexCountAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "select count(*) from pg_indexes where schemaname = 'public' and tablename = 'mt_events' and indexname = @name", connection);
        command.Parameters.AddWithValue("name", IndexName);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<string> MigrationAsync(string file) =>
        await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", "5.6.0", file), Ct);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "barakoCMS.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("barakoCMS.sln not found above the test output");
    }
}
