using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The schedule trigger. The clock is the <c>nowUtc</c> each sweep is handed, so no test waits for
/// a minute to pass.
/// </summary>
[Collection("Integration1")]
public class WorkflowScheduleTests
{
    private readonly IntegrationTestFixture _fixture;

    public WorkflowScheduleTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WorkflowDefinition Scheduled(string cron, string zone, DateTime armedAt) => new()
    {
        Id = Guid.NewGuid(),
        Name = "scheduled-" + Guid.NewGuid().ToString("N")[..8],
        TriggerContentType = "Article",
        TriggerEvent = WorkflowEvents.Schedule,
        TriggerEvents = [WorkflowEvents.Schedule],
        Schedule = cron,
        ScheduleTimeZone = zone,
        ScheduleArmedAt = armedAt,
        Actions = [new WorkflowAction { Type = "Email", Parameters = new() { ["To"] = "a@b.com", ["Subject"] = "s", ["Body"] = "b" } }],
    };

    private static DateTime Utc(int year, int month, int day, int hour, int minute, int second = 0) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    private async Task StoreAsync(WorkflowDefinition workflow, params ScheduleClaim[] claims)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(workflow);
        foreach (var claim in claims) session.Store(claim);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<int> FireAsync(DateTime now)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        return await WorkflowSchedule.FireDueAsync(session, now, null, Ct);
    }

    private async Task<List<WorkflowRun>> RunsOfAsync(Guid workflowId)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        return (await session.Query<WorkflowRun>().Where(r => r.WorkflowDefinitionId == workflowId).ToListAsync(Ct)).ToList();
    }

    [Fact]
    public async Task Midnight_in_Manila_fires_at_1600_UTC_with_the_previous_midnight_as_previousAt()
    {
        var workflow = Scheduled("0 0 * * *", "Asia/Manila", Utc(2026, 10, 19, 10, 0));
        await StoreAsync(workflow);

        await FireAsync(Utc(2026, 10, 19, 15, 59));
        (await RunsOfAsync(workflow.Id)).Should().BeEmpty("midnight in Manila has not arrived at 15:59 UTC");

        await FireAsync(Utc(2026, 10, 19, 16, 0, 30));
        var runs = await RunsOfAsync(workflow.Id);

        runs.Should().HaveCount(1);
        runs[0].TriggerEvent.Should().Be(WorkflowEvents.Schedule);
        runs[0].ScheduledFor.Should().Be(Utc(2026, 10, 19, 16, 0));
        runs[0].ScheduledPreviousFor.Should().Be(Utc(2026, 10, 18, 16, 0));
    }

    [Fact]
    public async Task A_schedule_saved_after_an_old_claim_does_not_fire_for_times_before_it_was_saved()
    {
        var workflow = Scheduled("0 * * * *", "UTC", Utc(2026, 10, 19, 11, 30));
        var old = new ScheduleClaim
        {
            Id = $"{workflow.Id:N}:20261015T1000Z", WorkflowId = workflow.Id, DueAt = Utc(2026, 10, 15, 10, 0),
            ClaimedAt = Utc(2026, 10, 15, 10, 0),
        };
        await StoreAsync(workflow, old);

        (await FireAsync(Utc(2026, 10, 19, 11, 45))).Should().Be(0, "10:00 and 11:00 are before the schedule was saved");
        (await RunsOfAsync(workflow.Id)).Should().BeEmpty();

        (await FireAsync(Utc(2026, 10, 19, 12, 1))).Should().Be(1);
        var runs = await RunsOfAsync(workflow.Id);
        runs.Should().HaveCount(1);
        runs[0].ScheduledFor.Should().Be(Utc(2026, 10, 19, 12, 0));
    }

    [Fact]
    public async Task A_host_stopped_over_two_due_times_fires_one_run_for_the_latest()
    {
        var workflow = Scheduled("0 * * * *", "UTC", Utc(2026, 10, 19, 9, 0));
        var tenth = new ScheduleClaim
        {
            Id = $"{workflow.Id:N}:20261019T1000Z", WorkflowId = workflow.Id, DueAt = Utc(2026, 10, 19, 10, 0),
            ClaimedAt = Utc(2026, 10, 19, 10, 0),
        };
        await StoreAsync(workflow, tenth);

        await FireAsync(Utc(2026, 10, 19, 12, 5));
        var runs = await RunsOfAsync(workflow.Id);

        runs.Should().HaveCount(1, "11:00 and 12:00 both passed and catch-up is once");
        runs[0].ScheduledFor.Should().Be(Utc(2026, 10, 19, 12, 0));
        runs[0].ScheduledPreviousFor.Should().Be(Utc(2026, 10, 19, 11, 0));

        await FireAsync(Utc(2026, 10, 19, 12, 6));
        (await RunsOfAsync(workflow.Id)).Should().HaveCount(1, "12:00 is claimed, so the next sweep has nothing new");
    }

    [Fact]
    public async Task Two_hosts_sweeping_the_same_due_time_fire_one_run()
    {
        var workflow = Scheduled("0 0 * * *", "UTC", Utc(2026, 10, 19, 12, 0));
        await StoreAsync(workflow);
        var now = Utc(2026, 10, 20, 0, 0, 10);

        await Task.WhenAll(FireAsync(now), FireAsync(now));

        var runs = await RunsOfAsync(workflow.Id);
        runs.Should().HaveCount(1);
        runs[0].ScheduledFor.Should().Be(Utc(2026, 10, 20, 0, 0));
    }

    [Fact]
    public void A_cron_expression_that_is_not_five_valid_fields_is_not_parsed()
    {
        WorkflowSchedule.Parse("0 0 * * *").Should().NotBeNull();
        WorkflowSchedule.Parse("*/15 9-17 * * 1-5").Should().NotBeNull();

        string?[] refused = [null, "", "   ", "* * * *", "0 0 0 * * *", "61 * * * *", "0 25 * * *", "every day", "0 0 * * * *"];
        refused.Should().HaveCount(9);
        refused.Should().OnlyContain(expression => WorkflowSchedule.Parse(expression) == null);
    }

    [Fact]
    public async Task Saving_an_invalid_cron_expression_is_refused_with_a_400()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_fixture);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var bad = Scheduled("not a cron", "Asia/Manila", DateTime.UtcNow);
        var response = await client.PostAsJsonAsync("/api/workflows", bad, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("cron");

        var badZone = Scheduled("0 0 * * *", "Mars/Olympus", DateTime.UtcNow);
        (await client.PostAsJsonAsync("/api/workflows", badZone, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void The_next_five_due_times_follow_the_schedule_in_its_zone()
    {
        var cron = WorkflowSchedule.Parse("0 0 * * *")!;
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");

        var next = WorkflowSchedule.Next(cron, zone, Utc(2026, 10, 19, 20, 0), WorkflowSchedule.PreviewCount);

        next.Should().HaveCount(5);
        next[0].Should().Be(Utc(2026, 10, 20, 16, 0));
        next[4].Should().Be(Utc(2026, 10, 24, 16, 0));
    }

    [Fact]
    public void The_schedule_variables_resolve_to_the_due_times()
    {
        var content = new ScheduledContent("Article", Utc(2026, 10, 19, 16, 0), Utc(2026, 10, 18, 16, 0));

        var text = barakoCMS.Infrastructure.Services.TemplateVariableExtractor.Resolve(
            "at={{schedule.at}} previous={{schedule.previousAt}}", content,
            barakoCMS.Infrastructure.Services.TemplateValueEncoding.None);

        text.Should().Contain("2026-10-19T16:00:00").And.Contain("2026-10-18T16:00:00");
        text.Should().NotContain("{{");
    }
}
