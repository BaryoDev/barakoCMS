using System.Net;
using System.Text.Json;
using barakoCMS.Features.Workflows.GetWorkflowHealth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using HealthEndpoint = barakoCMS.Features.Workflows.GetWorkflowHealth.Endpoint;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The tenant verdict on GET /api/workflows/health: runs in the last 24 hours, the newest ten, the
/// rate and the verdict. Each test reads a tenant of its own, so other tests' runs never count.
/// </summary>
[Collection("Integration1")]
public class WorkflowDayHealthTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly WorkflowStopHarness _harness;

    public WorkflowDayHealthTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _harness = new WorkflowStopHarness(fixture);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    private static string NewTenant() => "dayhealth-" + Guid.NewGuid().ToString("N");

    private async Task SeedAsync(
        string tenant, Guid workflowId, RunStatus status, int count, TimeSpan? age = null)
    {
        await using var session = Store.LightweightSession(tenant);
        for (var i = 0; i < count; i++)
        {
            session.Store(new WorkflowRun
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                WorkflowName = "day",
                ContentId = Guid.NewGuid(),
                ContentType = "article",
                TriggerEvent = WorkflowEvents.Published,
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow - (age ?? TimeSpan.FromMinutes(5)) - TimeSpan.FromSeconds(i),
            });
        }

        await session.SaveChangesAsync(Ct);
    }

    private async Task<HealthEndpoint.DayHealth> ReadAsync(string tenant)
    {
        await using var session = Store.QuerySession(tenant);
        return await HealthEndpoint.DayHealthAsync(session, DateTimeOffset.UtcNow, Ct);
    }

    [Theory]
    [InlineData(95, 5, "ok")]
    [InlineData(80, 20, "warn")]
    [InlineData(50, 50, "bad")]
    public async Task The_verdict_follows_the_success_rate(int succeeded, int failed, string verdict)
    {
        var tenant = NewTenant();
        var workflow = Guid.NewGuid();
        await SeedAsync(tenant, workflow, RunStatus.Succeeded, succeeded);
        await SeedAsync(tenant, workflow, RunStatus.Failed, failed);

        var health = await ReadAsync(tenant);

        health.Runs24h.Should().Be(100);
        health.Verdict.Should().Be(verdict);
        health.SuccessRate.Should().BeApproximately(succeeded / 100.0, 1e-9);
        health.Summary.Should().Contain($"{succeeded} of 100");
    }

    [Fact]
    public async Task No_runs_in_24_hours_is_ok_and_the_summary_says_so()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant, Guid.NewGuid(), RunStatus.Failed, 3, TimeSpan.FromHours(30));

        var health = await ReadAsync(tenant);

        health.Runs24h.Should().Be(0);
        health.SuccessRate.Should().BeNull();
        health.Verdict.Should().Be("ok");
        health.Summary.Should().Contain("No runs");
        health.Workflows.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelled_pending_and_running_runs_are_counted_but_left_out_of_the_rate()
    {
        var tenant = NewTenant();
        var workflow = Guid.NewGuid();
        await SeedAsync(tenant, workflow, RunStatus.Succeeded, 9);
        await SeedAsync(tenant, workflow, RunStatus.Failed, 1);
        await SeedAsync(tenant, workflow, RunStatus.Cancelled, 20);
        await SeedAsync(tenant, workflow, RunStatus.Running, 20);
        await SeedAsync(tenant, workflow, RunStatus.Pending, 20);

        var health = await ReadAsync(tenant);

        health.Runs24h.Should().Be(70);
        health.SuccessRate.Should().BeApproximately(0.9, 1e-9);
        health.Verdict.Should().Be("ok");
        health.Workflows.Should().HaveCount(1);
        health.Workflows[0].FinishedRuns.Should().Be(10);
    }

    [Fact]
    public async Task A_partially_failed_run_counts_against_the_rate_and_shows_as_warn()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant, Guid.NewGuid(), RunStatus.PartiallyFailed, 1);

        var health = await ReadAsync(tenant);

        health.Recent.Should().HaveCount(1);
        health.Recent[0].Outcome.Should().Be("warn");
        health.SuccessRate.Should().Be(0);
        health.Verdict.Should().Be("bad");
    }

    [Fact]
    public async Task Recent_is_the_newest_ten_with_their_own_status_and_an_outcome_only_where_counted()
    {
        var tenant = NewTenant();
        var workflow = Guid.NewGuid();
        await SeedAsync(tenant, workflow, RunStatus.Cancelled, 1, TimeSpan.FromMinutes(1));
        await SeedAsync(tenant, workflow, RunStatus.Succeeded, 12, TimeSpan.FromMinutes(10));

        var health = await ReadAsync(tenant);

        health.Recent.Should().HaveCount(10);
        health.Recent[0].Status.Should().Be("Cancelled");
        health.Recent[0].Outcome.Should().BeNull();
        health.Recent.Skip(1).Should().OnlyContain(r => r.Outcome == "ok");
    }

    [Fact]
    public async Task Another_tenants_runs_never_count()
    {
        var mine = NewTenant();
        var theirs = NewTenant();
        var workflow = Guid.NewGuid();
        await SeedAsync(mine, workflow, RunStatus.Succeeded, 10);
        await SeedAsync(theirs, workflow, RunStatus.Failed, 40);

        var health = await ReadAsync(mine);

        health.Runs24h.Should().Be(10);
        health.Recent.Should().HaveCount(10);
        health.Recent.Should().OnlyContain(r => r.Status == "Succeeded");
        health.Verdict.Should().Be("ok");
    }

    [Fact]
    public async Task Each_workflow_gets_its_own_rate_and_verdict()
    {
        var tenant = NewTenant();
        var good = Guid.NewGuid();
        var broken = Guid.NewGuid();
        await using (var session = Store.LightweightSession(tenant))
        {
            session.Store(new WorkflowDefinition { Id = good, Name = "a-good", TriggerContentType = "post", TriggerEvent = WorkflowEvents.Published });
            session.Store(new WorkflowDefinition { Id = broken, Name = "b-broken", TriggerContentType = "post", TriggerEvent = WorkflowEvents.Published });
            await session.SaveChangesAsync(Ct);
        }

        await SeedAsync(tenant, good, RunStatus.Succeeded, 10);
        await SeedAsync(tenant, broken, RunStatus.Succeeded, 1);
        await SeedAsync(tenant, broken, RunStatus.Failed, 3);

        var health = await ReadAsync(tenant);

        health.Workflows.Should().HaveCount(2);
        health.Workflows.Select(w => w.Name).Should().Equal("a-good", "b-broken");
        health.Workflows[0].Verdict.Should().Be("ok");
        health.Workflows[1].SuccessRate.Should().Be(0.25);
        health.Workflows[1].Verdict.Should().Be("bad");
    }

    [Theory]
    [InlineData(90, 100, "ok")]
    [InlineData(89, 100, "warn")]
    [InlineData(70, 100, "warn")]
    [InlineData(69, 100, "bad")]
    [InlineData(0, 0, "ok")]
    public void The_thresholds_are_90_and_70_percent_inclusive(int succeeded, int finished, string verdict)
    {
        WorkflowHealthRules.Verdict(succeeded, finished).Should().Be(verdict);
    }

    [Theory]
    [InlineData(899, 1000, "(89%)")]
    [InlineData(695, 1000, "(69%)")]
    [InlineData(900, 1000, "(90%)")]
    public void The_summary_percent_is_floored_so_it_never_reads_above_the_threshold_it_missed(int succeeded, int finished, string shown)
    {
        var verdict = WorkflowHealthRules.Verdict(succeeded, finished);

        WorkflowHealthRules.Summary(finished, succeeded, finished, verdict).Should().Contain(shown);
    }

    [Fact]
    public async Task A_run_exactly_24_hours_old_counts_and_one_second_older_does_not()
    {
        var tenant = NewTenant();
        var now = DateTimeOffset.UtcNow;
        await using (var session = Store.LightweightSession(tenant))
        {
            foreach (var age in new[] { TimeSpan.FromHours(24), TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1) })
            {
                session.Store(new WorkflowRun
                {
                    Id = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), WorkflowName = "edge",
                    ContentId = Guid.NewGuid(), ContentType = "article", TriggerEvent = WorkflowEvents.Published,
                    Status = RunStatus.Succeeded, CreatedAt = now - age,
                });
            }

            await session.SaveChangesAsync(Ct);
        }

        await using var read = Store.QuerySession(tenant);
        var health = await HealthEndpoint.DayHealthAsync(read, now, Ct);

        health.Runs24h.Should().Be(1);
    }

    [Fact]
    public async Task The_health_route_returns_the_health_shape_and_not_a_workflow_lookup()
    {
        var admin = await _harness.AdminAsync();

        var res = await admin.GetAsync("/api/workflows/health", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", body);
        using var doc = JsonDocument.Parse(body);
        foreach (var name in new[] { "runs24h", "recent", "successRate", "verdict", "summary", "workflows", "items" })
            doc.RootElement.TryGetProperty(name, out _).Should().BeTrue("the response should have {0}", name);
        doc.RootElement.GetProperty("verdict").GetString().Should().BeOneOf("ok", "warn", "bad");
    }
}
