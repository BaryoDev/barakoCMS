using barakoCMS.Extensions;
using FastEndpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// Job workers read storage only once the schema is in place (#686).
/// </summary>
/// <remarks>
/// <para>
/// <c>UseBarakoCMS</c> starts the job queue workers, and every host calls it before
/// <c>ApplyMartenSchemaAsync</c>. A worker's first poll queries <c>JobRecord</c>, and on a database
/// without the jobs table Marten creates it on the spot, outside <c>SchemaApplyLock</c>. The explicit
/// apply running beside it sees the same indexes missing, and whichever of the two issues its
/// <c>CREATE INDEX</c> second fails on <c>42P07: relation "mt_doc_jobs_idx_queue_id" already exists</c>.
/// </para>
/// <para>
/// The collision itself depends on timing. What it needs does not: a table created before the
/// locked apply ran. So that is what is asserted, on a database of its own so the table genuinely
/// does not exist when the host starts.
/// </para>
/// </remarks>
[Collection("Sequential")]
public class JobWorkerSchemaOrderTests
{
    private readonly IntegrationTestFixture _fixture;

    public JobWorkerSchemaOrderTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Job_workers_create_nothing_before_the_schema_is_applied()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await FreshDatabaseAsync(ct);
        await using var app = Build(connectionString);

        app.UseBarakoCMS();

        // An unheld worker polls as soon as it starts, so the table shows up within a second.
        var createdEarly = await EventuallyExistsAsync(connectionString, "mt_doc_jobs", TimeSpan.FromSeconds(5), ct);
        createdEarly.Should().BeFalse("nothing may create schema objects outside the locked apply");

        await app.ApplyMartenSchemaAsync();

        (await ExistsAsync(connectionString, "mt_doc_jobs", ct)).Should().BeTrue("the locked apply creates it");
    }

    /// <summary>
    /// The collision itself, made reliable rather than left to timing.
    /// </summary>
    /// <remarks>
    /// An event trigger sleeps inside any DDL on the jobs table issued on its own, which is what
    /// Marten's lazy path sends, and keeps that create uncommitted for a few seconds. The explicit
    /// apply sends the jobs table in one batch with the event store, so its DDL is never slowed. An
    /// apply that starts in that window does not see the table, issues its own create, waits on the
    /// uncommitted one, and fails once it commits. With nothing creating the table lazily, this passes.
    /// </remarks>
    [Fact]
    public async Task The_schema_apply_does_not_collide_with_a_slow_create_outside_the_lock()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await FreshDatabaseAsync(ct);
        await SlowUnlockedJobsDdlAsync(connectionString, ct);
        await using var app = Build(connectionString);

        app.UseBarakoCMS();
        var apply = async () => await app.ApplyMartenSchemaAsync();

        await apply.Should().NotThrowAsync("nothing may be creating the jobs table while the locked apply runs");
    }

    [Fact]
    public async Task A_host_that_never_applies_the_schema_still_runs_its_job_workers()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await FreshDatabaseAsync(ct);
        await using var app = Build(connectionString);

        app.UseBarakoCMS();
        await app.StartAsync(ct);

        try
        {
            // Only a worker's poll creates the jobs table here, so its existence is the proof one ran.
            var polled = await EventuallyExistsAsync(connectionString, "mt_doc_jobs", TimeSpan.FromSeconds(30), ct);
            polled.Should().BeTrue("a started host releases its workers even without ApplyMartenSchemaAsync");
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private WebApplication Build(string connectionString)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString,
            ["DATABASE_URL"] = string.Empty,
            ["JWT:Key"] = IntegrationTestFixture.JwtKey,
            ["Seed:DemoContent"] = "false",
        });
        builder.Services.AddBarakoCMS(builder.Configuration, modules => modules.Discover = false);
        builder.Services.AddFastEndpoints(o =>
        {
            o.DisableAutoDiscovery = true;
            o.Assemblies = [typeof(barakoCMS.Data.DataSeeder).Assembly];
        });
        return builder.Build();
    }

    private async Task<string> FreshDatabaseAsync(CancellationToken ct)
    {
        var name = "job_order_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        return new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    private static async Task SlowUnlockedJobsDdlAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            $"""
            create function slow_unlocked_jobs_ddl() returns event_trigger language plpgsql as $$
            begin
              if exists (select 1 from pg_event_trigger_ddl_commands() c where c.object_identity like '%mt_doc_jobs%')
                 and current_query() not ilike '%mt_events%' then
                perform pg_sleep(3);
              end if;
            end $$;
            create event trigger slow_unlocked_jobs_ddl on ddl_command_end execute function slow_unlocked_jobs_ddl();
            """,
            connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> ExistsAsync(string connectionString, string relation, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("select to_regclass(@name) is not null", connection);
        command.Parameters.AddWithValue("name", "public." + relation);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<bool> EventuallyExistsAsync(string connectionString, string relation, TimeSpan within, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (await ExistsAsync(connectionString, relation, ct))
                return true;
            await Task.Delay(100, ct);
        }

        return await ExistsAsync(connectionString, relation, ct);
    }
}
