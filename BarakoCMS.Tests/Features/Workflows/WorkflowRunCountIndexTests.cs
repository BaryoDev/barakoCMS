using barakoCMS.Features.WorkflowRuns;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The run list's status counts have to stay exact for every filter, and have indexes that carry the
/// status and the tenant so each capped count stays inside one index range.
/// </summary>
/// <remarks>
/// Production runs <c>AutoCreate.CreateOnly</c>, so an upgraded database gets the indexes from
/// the <c>migrations/5.7.2/workflow-run-*tenant-index.sql</c> files or not at all. The migration tests apply
/// each file to a copy of the table in a scratch schema and compare each index with the one Marten created.
/// </remarks>
[Collection("Integration1")]
public class WorkflowRunCountIndexTests
{
    private const string Table = "mt_doc_workflow_runs";
    private const string MigrationFolder = "5.7.2";

    private static readonly string[] Files =
    [
        "workflow-run-status-tenant-index.sql",
        "workflow-run-workflow-status-tenant-index.sql",
        "workflow-run-content-status-tenant-index.sql",
    ];

    public static TheoryData<string, string, string, string> Indexes => new()
    {
        { "mt_doc_workflow_runs_idx_status_tenant", "Status", "", Files[0] },
        { "mt_doc_workflow_runs_idx_workflow_status_tenant", "Status", "WorkflowDefinitionId", Files[1] },
        { "mt_doc_workflow_runs_idx_content_status_tenant", "Status", "ContentId", Files[2] },
    };

    private readonly IntegrationTestFixture _factory;
    private readonly WorkflowStopHarness _harness;

    public WorkflowRunCountIndexTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Counts_stay_exact_for_every_filter_as_runs_are_added_and_change_status()
    {
        var workflowA = await _harness.StoreWorkflowAsync();
        var workflowB = await _harness.StoreWorkflowAsync();
        var contentA = await _harness.StoreContentAsync();
        var contentB = await _harness.StoreContentAsync();

        var before = await CountsAsync(null, null);

        var first = await _harness.SeedRunAsync(workflowA, contentA, [WorkflowStopHarness.Waiting()]);
        await _harness.SeedRunAsync(workflowA, contentA, [WorkflowStopHarness.Waiting()]);
        await _harness.SeedRunAsync(workflowA, contentB, [WorkflowStopHarness.Finished(AttemptStatus.Succeeded)]);
        await _harness.SeedRunAsync(workflowB, contentB, [WorkflowStopHarness.Finished(AttemptStatus.Failed)]);

        var added = await CountsAsync(null, null);
        added["Pending"].Should().Be(before["Pending"] + 2);
        added["Succeeded"].Should().Be(before["Succeeded"] + 1);
        added["Failed"].Should().Be(before["Failed"] + 1);

        (await CountsAsync(null, workflowA)).Should().Contain(Exactly(pending: 2, succeeded: 1));
        (await CountsAsync(null, workflowB)).Should().Contain(Exactly(failed: 1));
        (await CountsAsync(contentA, null)).Should().Contain(Exactly(pending: 2));
        (await CountsAsync(contentB, null)).Should().Contain(Exactly(succeeded: 1, failed: 1));
        (await CountsAsync(contentB, workflowA)).Should().Contain(Exactly(succeeded: 1));

        await FinishAsync(first.Id, AttemptStatus.Succeeded);

        var moved = await CountsAsync(null, null);
        moved["Pending"].Should().Be(before["Pending"] + 1);
        moved["Succeeded"].Should().Be(before["Succeeded"] + 2);
        (await CountsAsync(null, workflowA)).Should().Contain(Exactly(pending: 1, succeeded: 2));
        (await CountsAsync(contentA, null)).Should().Contain(Exactly(pending: 1, succeeded: 1));
        (await CountsAsync(contentA, workflowB)).Should().Contain(Exactly());
    }

    [Fact]
    public async Task A_status_with_more_runs_than_the_cap_reports_the_cap_and_is_named()
    {
        var workflow = await _harness.StoreWorkflowAsync();
        var content = await _harness.StoreContentAsync();
        await _harness.SeedRunAsync(workflow, content, [WorkflowStopHarness.Waiting()]);
        await _harness.SeedRunAsync(workflow, content, [WorkflowStopHarness.Waiting()]);
        await _harness.SeedRunAsync(workflow, content, [WorkflowStopHarness.Finished(AttemptStatus.Failed)]);

        await using var session = _harness.Store.QuerySession();
        var counts = await RunQueries.CountByStatusAsync(session, content, workflow, Ct, cap: 1);

        counts.Counts["Pending"].Should().Be(1);
        counts.Counts["Failed"].Should().Be(1);
        counts.Capped.Should().BeEquivalentTo(["Pending"]);
    }

    [Theory]
    [MemberData(nameof(Indexes))]
    public async Task Marten_declares_the_index_on_the_status_and_the_tenant(string index, string status, string filter, string file)
    {
        _ = file;
        await _harness.StoreWorkflowAsync();

        await using var connection = await OpenAsync();
        var def = await IndexAsync(connection, "public", index);

        def.Should().NotBeNull("the mapping should declare {0}", index);
        def.Should().Contain($"'{status}'").And.Contain("tenant_id");
        if (filter.Length > 0) def.Should().Contain($"'{filter}'");
    }

    [Theory]
    [MemberData(nameof(Indexes))]
    public async Task The_hand_applied_migration_builds_the_index_Marten_builds(string index, string status, string filter, string file)
    {
        _ = (status, filter);
        var scratch = "run_count_check_" + Guid.NewGuid().ToString("N")[..8];
        var scoped = (await MigrationAsync(file)).Replace($"public.{Table}", $"{scratch}.{Table}");

        await using var connection = await OpenAsync();
        try
        {
            await PrepareScratchAsync(connection, scratch);
            await ExecuteAsync(connection, scoped);
            await ExecuteAsync(connection, scoped);

            var expected = await IndexAsync(connection, "public", index);
            var actual = await IndexAsync(connection, scratch, index);

            expected.Should().NotBeNull("the mapping should declare {0}", index);
            actual.Should().Be(expected, "the file has to create the index Marten creates, or the schema check reports it");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("and CAST(data ->> 'WorkflowDefinitionId' as uuid) = '00000000-0000-0000-0000-000000000001'")]
    [InlineData("and CAST(data ->> 'ContentId' as uuid) = '00000000-0000-0000-0000-000000000001'")]
    public async Task The_capped_count_query_can_read_a_status_and_tenant_index(string filter)
    {
        await _harness.StoreWorkflowAsync();

        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, "BEGIN");
        try
        {
            // Sequential scans are off so a near-empty table does not decide the plan. The statement is
            // the shape RunQueries.CountByStatusAsync sends: equality on the status, the tenant and the
            // optional filter, then the first StatusCountCap + 1 ids.
            await ExecuteAsync(connection, "SET LOCAL enable_seqscan = off");
            await using var command = new NpgsqlCommand(
                "EXPLAIN select id from public.mt_doc_workflow_runs " +
                $"where CAST(data ->> 'Status' as integer) = 0 and tenant_id = 'plan-check' {filter} limit {RunQueries.StatusCountCap + 1}",
                connection);
            var plan = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync(Ct))
            {
                while (await reader.ReadAsync(Ct)) plan.Add(reader.GetString(0));
            }

            string.Join("\n", plan).Should().MatchRegex("mt_doc_workflow_runs_idx_\\w*tenant", "the count should read a status and tenant index");
        }
        finally
        {
            await ExecuteAsync(connection, "ROLLBACK");
        }
    }

    [Fact]
    public async Task The_rollback_drops_every_index_the_migration_builds()
    {
        var scratch = "run_count_roll_" + Guid.NewGuid().ToString("N")[..8];
        var migrate = new List<string>();
        var rollback = new List<string>();
        foreach (var file in Files)
        {
            migrate.Add((await MigrationAsync(file)).Replace($"public.{Table}", $"{scratch}.{Table}"));
            rollback.Add((await MigrationAsync("rollback-" + file)).Replace("public.", $"{scratch}."));
        }

        await using var connection = await OpenAsync();
        try
        {
            await PrepareScratchAsync(connection, scratch);
            foreach (var sql in migrate) await ExecuteAsync(connection, sql);
            (await ScratchIndexNamesAsync(connection, scratch)).Should().HaveCount(3);

            foreach (var sql in rollback) await ExecuteAsync(connection, sql);
            foreach (var sql in rollback) await ExecuteAsync(connection, sql);

            (await ScratchIndexNamesAsync(connection, scratch)).Should().BeEmpty();
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    private async Task<Dictionary<string, int>> CountsAsync(Guid? contentId, Guid? workflowId)
    {
        await using var session = _harness.Store.QuerySession();
        return (await RunQueries.CountByStatusAsync(session, contentId, workflowId, Ct)).Counts;
    }

    private static Dictionary<string, int> Exactly(int pending = 0, int succeeded = 0, int failed = 0) => new()
    {
        ["Pending"] = pending,
        ["Running"] = 0,
        ["Succeeded"] = succeeded,
        ["Failed"] = failed,
        ["PartiallyFailed"] = 0,
        ["Cancelled"] = 0,
    };

    private async Task FinishAsync(Guid runId, AttemptStatus status)
    {
        await using var session = _harness.Store.LightweightSession();
        var run = await session.LoadAsync<WorkflowRun>(runId, Ct);
        run.Should().NotBeNull();

        foreach (var attempt in run!.Actions)
        {
            attempt.Status = status;
            attempt.Attempts = 1;
            attempt.CompletedAt = DateTimeOffset.UtcNow;
        }

        run.Recompute();
        session.Store(run);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task PrepareScratchAsync(NpgsqlConnection connection, string scratch)
    {
        await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}");
        await ExecuteAsync(connection, $"CREATE TABLE {scratch}.{Table} (LIKE public.{Table} INCLUDING DEFAULTS)");
    }

    private static async Task<string?> IndexAsync(NpgsqlConnection connection, string schema, string index)
    {
        await using var command = new NpgsqlCommand(
            "select indexdef from pg_indexes where schemaname = @schema and tablename = @table and indexname = @index", connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", Table);
        command.Parameters.AddWithValue("index", index);
        var def = (string?)await command.ExecuteScalarAsync(Ct);
        return def?.Replace($"{schema}.{Table}", Table);
    }

    private static async Task<List<string>> ScratchIndexNamesAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand(
            "select indexname from pg_indexes where schemaname = @schema and tablename = @table order by indexname", connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", Table);
        await using var reader = await command.ExecuteReaderAsync(Ct);

        var names = new List<string>();
        while (await reader.ReadAsync(Ct)) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<string> MigrationAsync(string file)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", MigrationFolder, file), Ct);
        sql.Should().Contain("public.", "the file should name the table as public.{0}", Table);
        return sql;
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
