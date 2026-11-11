using System.Net;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// <c>GET /api/workflow-runs</c> with a window: <c>statusCounts</c>, the previous window and a series.
/// </summary>
/// <remarks>
/// Runs sit at known stamps counted back from today's midnight, in statuses that differ, with a
/// second workflow's runs inside the window so a count that ignored the workflow filter comes out
/// larger. Only terminal statuses are seeded, so the hosted runner leaves them alone.
/// </remarks>
[Collection("Integration1")]
public class WorkflowRunCountsWindowTests
{
    private static readonly DateTime Base = DateTime.UtcNow.Date.AddDays(-5);
    private static readonly DateTime From = Base;
    private static readonly DateTime To = Base.AddDays(3);

    private readonly WorkflowStopHarness _harness;

    public WorkflowRunCountsWindowTests(IntegrationTestFixture factory) => _harness = new WorkflowStopHarness(factory);

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    private static string Q(DateTime t) => Uri.EscapeDataString(t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));

    private static string WindowQuery => $"from={Q(From)}&to={Q(To)}";

    /// <summary>
    /// Day 0 is the first day of the window. A run before the previous window, two in it, then in
    /// the window: two on day 0, one on day 1, one on day 2, and one at the end instant, outside.
    /// </summary>
    private async Task<Guid> SeedAsync()
    {
        var workflow = Guid.NewGuid();
        var content = await _harness.StoreContentAsync();
        var rows = new (TimeSpan After, RunStatus Status, Guid Workflow)[]
        {
            (TimeSpan.FromDays(-5), RunStatus.Succeeded, workflow),
            (TimeSpan.FromDays(-2), RunStatus.Failed, workflow),
            (TimeSpan.Zero, RunStatus.Succeeded, workflow),
            (TimeSpan.FromHours(12), RunStatus.Failed, workflow),
            (TimeSpan.FromHours(27), RunStatus.Succeeded, workflow),
            (TimeSpan.FromHours(71), RunStatus.Cancelled, workflow),
            (TimeSpan.FromDays(3), RunStatus.Succeeded, workflow),
            (TimeSpan.FromHours(5), RunStatus.Failed, Guid.NewGuid()),
            (TimeSpan.FromHours(6), RunStatus.Failed, Guid.NewGuid()),
        };

        await using var session = _harness.Store.LightweightSession();
        var sequence = 0;
        foreach (var (after, status, wf) in rows)
        {
            session.Store(new WorkflowRun
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = wf,
                WorkflowName = "Counted",
                ContentId = content,
                ContentType = "article",
                TriggerEvent = "Published",
                TriggeringEventSequence = ++sequence,
                Status = status,
                CreatedAt = new DateTimeOffset(From + after, TimeSpan.Zero),
            });
        }

        await session.SaveChangesAsync(Ct);
        return workflow;
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string query)
    {
        var admin = await _harness.AdminAsync();
        var response = await admin.GetAsync($"/api/workflow-runs?{query}", Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<JsonElement> ListAsync(string query)
    {
        var (status, body) = await GetAsync(query);
        status.Should().Be(HttpStatusCode.OK, body);
        using var parsed = JsonDocument.Parse(body);
        return parsed.RootElement.Clone();
    }

    private static Dictionary<string, int> Counts(JsonElement element) =>
        element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());

    [Fact]
    public async Task Without_a_window_the_counts_cover_every_run_of_the_workflow_and_no_windowed_field_appears()
    {
        var workflow = await SeedAsync();

        var list = await ListAsync($"workflowId={workflow}");

        var counts = Counts(list.GetProperty("statusCounts"));
        counts["Succeeded"].Should().Be(4);
        counts["Failed"].Should().Be(2);
        counts["Cancelled"].Should().Be(1);
        counts["Pending"].Should().Be(0);
        list.EnumerateObject().Select(p => p.Name).Should().NotContain(["window", "previousStatusCounts", "series"],
            "a response with no window parameter carries none of the windowed fields");
    }

    [Fact]
    public async Task A_window_counts_the_runs_created_in_it_and_matches_a_direct_query()
    {
        var workflow = await SeedAsync();

        var list = await ListAsync($"workflowId={workflow}&{WindowQuery}");

        var counts = Counts(list.GetProperty("statusCounts"));
        counts.Should().Contain(new KeyValuePair<string, int>("Succeeded", 2));
        counts.Should().Contain(new KeyValuePair<string, int>("Failed", 1));
        counts.Should().Contain(new KeyValuePair<string, int>("Cancelled", 1));
        counts["Pending"].Should().Be(0, "every status is present");

        var from = new DateTimeOffset(From, TimeSpan.Zero);
        var to = new DateTimeOffset(To, TimeSpan.Zero);
        await using var session = _harness.Store.QuerySession();
        var direct = await session.Query<WorkflowRun>()
            .Where(r => r.WorkflowDefinitionId == workflow && r.CreatedAt >= from && r.CreatedAt < to)
            .CountAsync(Ct);
        counts.Values.Sum().Should().Be(direct);

        var window = list.GetProperty("window");
        window.GetProperty("from").GetDateTimeOffset().UtcDateTime.Should().Be(From);
        window.GetProperty("to").GetDateTimeOffset().UtcDateTime.Should().Be(To);
    }

    [Fact]
    public async Task The_previous_window_is_counted_beside_it()
    {
        var workflow = await SeedAsync();

        var list = await ListAsync($"workflowId={workflow}&{WindowQuery}");

        var previous = Counts(list.GetProperty("previousStatusCounts"));
        previous["Failed"].Should().Be(1, "two days before the window, inside the three days before it");
        previous["Succeeded"].Should().Be(0, "the run five days before is before the previous window");
    }

    [Fact]
    public async Task A_day_series_has_a_bucket_per_day_with_the_counts_by_status()
    {
        var workflow = await SeedAsync();

        var list = await ListAsync($"workflowId={workflow}&{WindowQuery}&series=day");

        var series = list.GetProperty("series").EnumerateArray().ToList();
        series.Should().NotBeEmpty();
        series.Should().HaveCount(3);
        series.Select(b => b.GetProperty("start").GetDateTimeOffset().UtcDateTime).Should().Equal(From, From.AddDays(1), From.AddDays(2));

        series.Select(b => b.GetProperty("total").GetInt32()).Should().Equal(2, 1, 1);
        Counts(series[0].GetProperty("counts")).Should().Contain(new KeyValuePair<string, int>("Succeeded", 1))
            .And.Contain(new KeyValuePair<string, int>("Failed", 1));
        Counts(series[1].GetProperty("counts"))["Succeeded"].Should().Be(1);
        Counts(series[2].GetProperty("counts"))["Cancelled"].Should().Be(1);
    }

    [Fact]
    public async Task A_range_counts_back_from_now()
    {
        var content = await _harness.StoreContentAsync();
        var workflow = Guid.NewGuid();
        await using (var session = _harness.Store.LightweightSession())
        {
            session.Store(new WorkflowRun
            {
                Id = Guid.NewGuid(), WorkflowDefinitionId = workflow, WorkflowName = "Counted", ContentId = content,
                ContentType = "article", TriggerEvent = "Published", TriggeringEventSequence = 1,
                Status = RunStatus.Failed, CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            });
            session.Store(new WorkflowRun
            {
                Id = Guid.NewGuid(), WorkflowDefinitionId = workflow, WorkflowName = "Counted", ContentId = content,
                ContentType = "article", TriggerEvent = "Published", TriggeringEventSequence = 2,
                Status = RunStatus.Failed, CreatedAt = DateTimeOffset.UtcNow.AddDays(-3),
            });
            await session.SaveChangesAsync(Ct);
        }

        Counts((await ListAsync($"workflowId={workflow}&range=24h")).GetProperty("statusCounts"))["Failed"].Should().Be(1);
        Counts((await ListAsync($"workflowId={workflow}&range=7d")).GetProperty("statusCounts"))["Failed"].Should().Be(2);
    }

    [Fact]
    public async Task A_poll_with_a_cursor_still_carries_no_counts_and_no_window()
    {
        var workflow = await SeedAsync();

        var list = await ListAsync($"workflowId={workflow}&changedSince=2019-12-31T00:00:00Z&range=7d");

        list.GetProperty("statusCounts").ValueKind.Should().Be(JsonValueKind.Null);
        list.EnumerateObject().Select(p => p.Name).Should().NotContain(["window", "previousStatusCounts", "series"]);
    }

    [Theory]
    [InlineData("range=1h", "Range must be one of")]
    [InlineData("range=7d&to=2026-03-13T00:00:00Z", "not both")]
    [InlineData("from=2026-03-13T00:00:00Z&to=2026-03-10T00:00:00Z", "From must be before to")]
    [InlineData("from=2025-12-01T00:00:00Z&to=2026-03-13T00:00:00Z", "at most 90 days")]
    [InlineData("series=day", "Series needs a window")]
    [InlineData("range=30d&series=hour", "at most 200 buckets")]
    public async Task A_window_past_a_cap_or_badly_formed_is_refused_with_400(string query, string reason)
    {
        var (status, body) = await GetAsync(query);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(reason);
    }

    [Fact]
    public async Task A_caller_without_the_capability_is_refused_a_windowed_count_too()
    {
        var client = await _harness.WithoutTheCapabilityAsync();

        var response = await client.GetAsync($"/api/workflow-runs?{WindowQuery}&series=day", Ct);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }
}
