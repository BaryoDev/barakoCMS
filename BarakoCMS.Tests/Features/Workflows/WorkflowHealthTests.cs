using System.Net;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// GET /api/workflows/health and the workflow filter on GET /api/workflow-runs.
/// </summary>
/// <remarks>
/// Each test makes its own workflow, named to sort first, so the page it asks for holds it however
/// many workflows other tests left behind.
/// </remarks>
[Collection("Integration1")]
public class WorkflowHealthTests
{
    private readonly WorkflowStopHarness _harness;

    public WorkflowHealthTests(IntegrationTestFixture factory) => _harness = new WorkflowStopHarness(factory);

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    private Task<Guid> WorkflowAsync() =>
        _harness.StoreWorkflowAsync(w => w.Name = $"000health-{Guid.NewGuid():N}");

    private async Task<WorkflowRun> RunAsync(
        Guid workflowId, RunStatus status, DateTimeOffset? createdAt = null, params long?[] durations)
    {
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            WorkflowName = "health",
            ContentId = Guid.NewGuid(),
            ContentType = "article",
            TriggerEvent = WorkflowEvents.Published,
            Status = status,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };
        for (var i = 0; i < durations.Length; i++)
        {
            run.Actions.Add(new WorkflowActionAttempt
            {
                Ordinal = i, ActionType = WorkflowStopHarness.Counting, DurationMs = durations[i],
                IdempotencyKey = $"{run.Id:N}-{i}",
            });
        }

        await using var session = _harness.Store.LightweightSession();
        session.Store(run);
        await session.SaveChangesAsync(Ct);
        return run;
    }

    private async Task<(JsonElement Workflow, JsonDocument Doc)> HealthOfAsync(Guid workflowId, string query = "")
    {
        var admin = await _harness.AdminAsync();
        var res = await admin.GetAsync($"/api/workflows/health?pageSize=100&{query}", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", body);

        var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty();
        var mine = items.Single(i => i.GetProperty("workflowId").GetGuid() == workflowId);
        return (mine, doc);
    }

    [Fact]
    public async Task The_rate_is_succeeded_over_succeeded_failed_and_partially_failed_only()
    {
        var id = await WorkflowAsync();
        await RunAsync(id, RunStatus.Succeeded);
        await RunAsync(id, RunStatus.Succeeded);
        await RunAsync(id, RunStatus.Failed);
        await RunAsync(id, RunStatus.PartiallyFailed);
        await RunAsync(id, RunStatus.Pending);
        await RunAsync(id, RunStatus.Running);
        await RunAsync(id, RunStatus.Cancelled);

        var (health, doc) = await HealthOfAsync(id);
        using var _ = doc;

        health.GetProperty("finishedRuns").GetInt32().Should().Be(4);
        health.GetProperty("successRate").GetDouble().Should().Be(0.5);
        var outcomes = health.GetProperty("lastOutcomes").EnumerateArray()
            .Select(o => o.GetProperty("status").GetString()).ToList();
        outcomes.Should().NotBeEmpty();
        outcomes.Should().NotContain(["Pending", "Running"]);
    }

    [Fact]
    public async Task A_workflow_with_no_finished_runs_has_a_null_rate()
    {
        var id = await WorkflowAsync();
        await RunAsync(id, RunStatus.Pending);
        await RunAsync(id, RunStatus.Cancelled);

        var (health, doc) = await HealthOfAsync(id);
        using var _ = doc;

        health.GetProperty("successRate").ValueKind.Should().Be(JsonValueKind.Null);
        health.GetProperty("finishedRuns").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Attempts_with_no_duration_are_left_out_of_the_average()
    {
        var id = await WorkflowAsync();
        await RunAsync(id, RunStatus.Succeeded, null, 100, null);
        await RunAsync(id, RunStatus.Succeeded, null, 300, null, null);

        var (health, doc) = await HealthOfAsync(id);
        using var _ = doc;

        health.GetProperty("averageActionDurationMs").GetDouble().Should().Be(200);
    }

    [Fact]
    public async Task Only_the_last_ten_finished_runs_are_listed_newest_first()
    {
        var id = await WorkflowAsync();
        var now = DateTimeOffset.UtcNow;
        var made = new List<WorkflowRun>();
        for (var i = 0; i < 12; i++) made.Add(await RunAsync(id, RunStatus.Succeeded, now.AddMinutes(-i - 1)));

        var (health, doc) = await HealthOfAsync(id);
        using var _ = doc;

        var ids = health.GetProperty("lastOutcomes").EnumerateArray().Select(o => o.GetProperty("runId").GetGuid()).ToList();
        ids.Should().Equal(made.Take(10).Select(r => r.Id));
    }

    [Fact]
    public async Task A_window_longer_than_retention_is_clamped_and_the_response_says_so()
    {
        var id = await WorkflowAsync();
        await RunAsync(id, RunStatus.Succeeded);

        var (_, defaulted) = await HealthOfAsync(id);
        using var d = defaulted;
        var cap = d.RootElement.GetProperty("windowDays").GetInt32();
        cap.Should().BeGreaterThan(0);

        var (_, asked) = await HealthOfAsync(id, $"windowDays={cap + 365}");
        using var a = asked;
        a.RootElement.GetProperty("windowDays").GetInt32().Should().BeLessThanOrEqualTo(90);

        var (_, tiny) = await HealthOfAsync(id, "windowDays=0");
        using var t = tiny;
        t.RootElement.GetProperty("windowDays").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_run_older_than_the_window_does_not_count_toward_the_rate()
    {
        var id = await WorkflowAsync();
        await RunAsync(id, RunStatus.Succeeded);
        await RunAsync(id, RunStatus.Failed, DateTimeOffset.UtcNow.AddDays(-30));

        var (health, doc) = await HealthOfAsync(id, "windowDays=7");
        using var _ = doc;

        health.GetProperty("finishedRuns").GetInt32().Should().Be(1);
        health.GetProperty("successRate").GetDouble().Should().Be(1.0);
    }

    [Fact]
    public async Task A_caller_without_view_workflow_runs_gets_403_from_the_health_route()
    {
        var without = await _harness.WithoutTheCapabilityAsync();

        (await without.GetAsync("/api/workflows/health", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_run_list_filters_by_workflow_and_its_status_counts_follow_the_filter()
    {
        var mine = await WorkflowAsync();
        var other = await WorkflowAsync();
        var kept = await RunAsync(mine, RunStatus.Succeeded);
        await RunAsync(mine, RunStatus.Failed);
        await RunAsync(other, RunStatus.Failed);
        await RunAsync(other, RunStatus.Failed);

        var admin = await _harness.AdminAsync();
        var res = await admin.GetAsync($"/api/workflow-runs?workflowId={mine}&status=Succeeded", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));

        var ids = doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(kept.Id);

        var counts = doc.RootElement.GetProperty("statusCounts");
        counts.GetProperty("Succeeded").GetInt32().Should().Be(1);
        counts.GetProperty("Failed").GetInt32().Should().Be(1, "the other workflow's two failures are not counted");
    }

    [Fact]
    public async Task Runs_of_the_same_workflow_in_another_tenant_are_not_counted_listed_or_averaged()
    {
        var id = await WorkflowAsync();
        var own = await RunAsync(id, RunStatus.Succeeded, null, 100);

        var otherTenant = "other-" + Guid.NewGuid().ToString("N")[..8];
        await using (var session = _harness.Store.LightweightSession(otherTenant))
        {
            for (var i = 0; i < 3; i++)
            {
                var run = new WorkflowRun
                {
                    Id = Guid.NewGuid(), WorkflowDefinitionId = id, WorkflowName = "health",
                    ContentId = Guid.NewGuid(), ContentType = "article", TriggerEvent = WorkflowEvents.Published,
                    Status = RunStatus.Failed,
                };
                run.Actions.Add(new WorkflowActionAttempt
                {
                    Ordinal = 0, ActionType = WorkflowStopHarness.Counting, DurationMs = 9000,
                    IdempotencyKey = $"{run.Id:N}-0",
                });
                session.Store(run);
            }

            await session.SaveChangesAsync(Ct);
        }

        var (health, doc) = await HealthOfAsync(id);
        using var _ = doc;

        health.GetProperty("finishedRuns").GetInt32().Should().Be(1, "only the caller's tenant is counted");
        health.GetProperty("successRate").GetDouble().Should().Be(1.0);
        health.GetProperty("averageActionDurationMs").GetDouble().Should().Be(100);
        var outcomes = health.GetProperty("lastOutcomes").EnumerateArray().ToList();
        outcomes.Should().HaveCount(1);
        outcomes[0].GetProperty("runId").GetGuid().Should().Be(own.Id);

        var admin = await _harness.AdminAsync();
        var res = await admin.GetAsync($"/api/workflow-runs?workflowId={id}", Ct);
        using var list = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        var counts = list.RootElement.GetProperty("statusCounts");
        counts.GetProperty("Succeeded").GetInt32().Should().Be(1);
        counts.GetProperty("Failed").GetInt32().Should().Be(0, "the three failures belong to another tenant");
    }
}
