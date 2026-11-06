using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using Xunit;
using ConfigCallers = BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests;

/// <summary>
/// Catch-up policies, daylight saving and pausing for a workflow schedule. The clock is the
/// <c>nowUtc</c> each sweep is handed.
/// </summary>
[Collection("Integration1")]
public class WorkflowScheduleCatchUpTests
{
    private readonly IntegrationTestFixture _fixture;

    public WorkflowScheduleCatchUpTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Utc(int year, int month, int day, int hour, int minute, int second = 0) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    private static WorkflowDefinition Scheduled(
        string cron, string zone, DateTime armedAt, string? catchUp = null, bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        Name = "catchup-" + Guid.NewGuid().ToString("N")[..8],
        TriggerContentType = "Article",
        TriggerEvent = WorkflowEvents.Schedule,
        TriggerEvents = [WorkflowEvents.Schedule],
        Schedule = cron,
        ScheduleTimeZone = zone,
        ScheduleCatchUp = catchUp,
        ScheduleArmedAt = armedAt,
        Enabled = enabled,
        Actions = [new WorkflowAction { Type = "Email", Parameters = new() { ["To"] = "a@b.com", ["Subject"] = "s", ["Body"] = "b" } }],
    };

    // The sweep covers every enabled scheduled workflow in the shared tenant, so workflows other
    // tests left behind also fire. A count is therefore taken over this test's own workflows.
    private readonly List<Guid> _own = [];

    private async Task StoreAsync(WorkflowDefinition workflow)
    {
        _own.Add(workflow.Id);
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(workflow);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<int> FireAsync(DateTime now)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var before = await OwnRunCountAsync(session);
        await WorkflowSchedule.FireDueAsync(session, now, null, Ct);
        return await OwnRunCountAsync(session) - before;
    }

    private async Task<int> OwnRunCountAsync(IDocumentSession session)
    {
        var own = _own.ToArray();
        return await session.Query<WorkflowRun>().CountAsync(r => own.Contains(r.WorkflowDefinitionId), Ct);
    }

    private async Task<List<WorkflowRun>> RunsOfAsync(Guid workflowId)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        return (await session.Query<WorkflowRun>().Where(r => r.WorkflowDefinitionId == workflowId).ToListAsync(Ct))
            .OrderBy(r => r.ScheduledFor).ToList();
    }

    [Fact]
    public async Task Each_fires_a_run_for_every_missed_time_oldest_first_with_the_time_before_as_previousAt()
    {
        var workflow = Scheduled("0 * * * *", "UTC", Utc(2026, 10, 19, 9, 0), WorkflowSchedule.CatchUpEach);
        await StoreAsync(workflow);

        (await FireAsync(Utc(2026, 10, 19, 12, 5))).Should().Be(3);
        var runs = await RunsOfAsync(workflow.Id);

        runs.Should().HaveCount(3, "10:00, 11:00 and 12:00 all passed; under once this is one run");
        runs.Select(r => r.ScheduledFor).Should().Equal(Utc(2026, 10, 19, 10, 0), Utc(2026, 10, 19, 11, 0), Utc(2026, 10, 19, 12, 0));
        runs.Select(r => r.ScheduledPreviousFor).Should().Equal(Utc(2026, 10, 19, 9, 0), Utc(2026, 10, 19, 10, 0), Utc(2026, 10, 19, 11, 0));

        (await FireAsync(Utc(2026, 10, 19, 12, 6))).Should().Be(0);
        (await RunsOfAsync(workflow.Id)).Should().HaveCount(3, "every time is claimed, so a second sweep fires nothing");
    }

    [Fact]
    public async Task Once_fires_one_run_where_each_would_fire_two()
    {
        var once = Scheduled("0 * * * *", "UTC", Utc(2026, 10, 19, 9, 0), WorkflowSchedule.CatchUpOnce);
        var each = Scheduled("0 * * * *", "UTC", Utc(2026, 10, 19, 9, 0), "EACH");
        await StoreAsync(once);
        await StoreAsync(each);

        await FireAsync(Utc(2026, 10, 19, 11, 5));

        (await RunsOfAsync(once.Id)).Should().HaveCount(1);
        (await RunsOfAsync(each.Id)).Should().HaveCount(2, "the policy name is not case sensitive");
    }

    [Fact]
    public async Task Each_is_capped_to_the_latest_missed_times()
    {
        var cap = WorkflowSchedule.MaxCatchUpRuns;
        var armed = Utc(2026, 10, 1, 0, 0);
        var workflow = Scheduled("0 * * * *", "UTC", armed, WorkflowSchedule.CatchUpEach);
        await StoreAsync(workflow);

        var now = armed.AddHours(cap + 10).AddMinutes(5);
        (await FireAsync(now)).Should().Be(cap);
        var runs = await RunsOfAsync(workflow.Id);

        runs.Should().HaveCount(cap);
        runs[^1].ScheduledFor.Should().Be(armed.AddHours(cap + 10), "the latest time is always kept");
        runs[0].ScheduledFor.Should().Be(armed.AddHours(11), "the oldest ten missed times are dropped");
        runs[0].ScheduledPreviousFor.Should().Be(armed.AddHours(10));

        (await FireAsync(now.AddMinutes(1))).Should().Be(0, "the dropped times are not caught up on the next sweep");
    }

    [Fact]
    public async Task Skip_fires_no_run_for_a_time_missed_but_fires_the_next_one_on_time()
    {
        var workflow = Scheduled("0 * * * *", "UTC", Utc(2026, 10, 19, 9, 0), WorkflowSchedule.CatchUpSkip);
        await StoreAsync(workflow);

        (await FireAsync(Utc(2026, 10, 19, 12, 30))).Should().Be(0, "10:00, 11:00 and 12:00 are all more than the on-time window old");
        (await RunsOfAsync(workflow.Id)).Should().BeEmpty();

        (await FireAsync(Utc(2026, 10, 19, 13, 0, 20))).Should().Be(1);
        var runs = await RunsOfAsync(workflow.Id);
        runs.Should().HaveCount(1);
        runs[0].ScheduledFor.Should().Be(Utc(2026, 10, 19, 13, 0));
        runs[0].ScheduledPreviousFor.Should().Be(Utc(2026, 10, 19, 12, 0));
    }

    [Fact]
    public async Task A_catch_up_policy_other_than_the_three_is_refused_with_a_400()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_fixture);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var bad = Scheduled("0 0 * * *", "Asia/Manila", DateTime.UtcNow, "always");
        var response = await client.PostAsJsonAsync("/api/workflows", bad, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("scheduleCatchUp");

        foreach (var policy in new[] { "once", "each", "skip" })
        {
            var good = Scheduled("0 0 * * *", "Asia/Manila", DateTime.UtcNow, policy);
            (await client.PostAsJsonAsync("/api/workflows", good, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public void A_time_that_does_not_exist_in_the_zone_fires_once_at_the_next_valid_minute()
    {
        var cron = WorkflowSchedule.Parse("30 2 * * *")!;
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // 2026-03-08: clocks go from 02:00 to 03:00, so 02:30 does not exist.
        var next = WorkflowSchedule.Next(cron, zone, Utc(2026, 3, 7, 12, 0), 3);

        next.Should().HaveCount(3);
        next[0].Should().Be(Utc(2026, 3, 8, 7, 0), "03:00 EDT, the first minute after the gap");
        next[1].Should().Be(Utc(2026, 3, 9, 6, 30), "02:30 EDT the day after");
        next.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_time_that_occurs_twice_in_the_zone_fires_once()
    {
        var cron = WorkflowSchedule.Parse("30 1 * * *")!;
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // 2026-11-01: clocks go from 02:00 back to 01:00, so 01:30 happens at 05:30Z and again at 06:30Z.
        var next = WorkflowSchedule.Next(cron, zone, Utc(2026, 10, 31, 12, 0), 3);

        next.Should().HaveCount(3);
        next[0].Should().Be(Utc(2026, 11, 1, 5, 30));
        next[1].Should().Be(Utc(2026, 11, 2, 6, 30), "the second 01:30 on the 1st is not a due time");
        next.Should().NotContain(Utc(2026, 11, 1, 6, 30));
    }

    [Fact]
    public async Task A_sweep_over_the_skipped_local_time_queues_one_run_for_the_day()
    {
        var workflow = Scheduled("30 2 * * *", "America/New_York", Utc(2026, 3, 7, 12, 0));
        await StoreAsync(workflow);

        await FireAsync(Utc(2026, 3, 8, 7, 0, 30));
        await FireAsync(Utc(2026, 3, 8, 8, 30));

        var runs = await RunsOfAsync(workflow.Id);
        runs.Should().HaveCount(1);
        runs[0].ScheduledFor.Should().Be(Utc(2026, 3, 8, 7, 0));
    }

    [Fact]
    public async Task A_sweep_over_the_repeated_local_time_queues_one_run_for_the_day()
    {
        var workflow = Scheduled("30 1 * * *", "America/New_York", Utc(2026, 10, 31, 12, 0), WorkflowSchedule.CatchUpEach);
        await StoreAsync(workflow);

        await FireAsync(Utc(2026, 11, 1, 5, 31));
        await FireAsync(Utc(2026, 11, 1, 6, 31));
        await FireAsync(Utc(2026, 11, 1, 8, 0));

        var runs = await RunsOfAsync(workflow.Id);
        runs.Should().HaveCount(1, "even under each, the second 01:30 is not a separate due time");
        runs[0].ScheduledFor.Should().Be(Utc(2026, 11, 1, 5, 30));
    }

    [Fact]
    public async Task A_paused_schedule_starts_no_runs()
    {
        var workflow = Scheduled("0 * * * *", "UTC", Utc(2026, 10, 19, 9, 0), WorkflowSchedule.CatchUpEach, enabled: false);
        await StoreAsync(workflow);

        (await FireAsync(Utc(2026, 10, 19, 12, 5))).Should().Be(0);
        (await RunsOfAsync(workflow.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_a_schedule_back_on_does_not_catch_up_for_the_time_it_was_off()
    {
        var workflow = Scheduled("0 * * * *", "UTC", Utc(2026, 10, 19, 9, 0), WorkflowSchedule.CatchUpEach, enabled: false);
        await StoreAsync(workflow);

        var (token, _) = await TestHelpers.CreateAdminUserAsync(_fixture);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var before = DateTime.UtcNow;
        var response = await client.PutAsJsonAsync($"/api/workflows/{workflow.Id}/enabled", new { enabled = true }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await session.LoadAsync<WorkflowDefinition>(workflow.Id, Ct);

        stored!.Enabled.Should().BeTrue();
        stored.ScheduleArmedAt.Should().BeOnOrAfter(before, "the weeks the schedule was off are not owed runs");
        stored.Schedule.Should().Be("0 * * * *", "pausing never deleted the schedule");
    }

    [Fact]
    public async Task Skip_records_the_newest_skipped_time_so_the_next_sweep_starts_after_it()
    {
        var workflow = Scheduled("0 * * * *", "UTC", Utc(2026, 10, 19, 9, 0), WorkflowSchedule.CatchUpSkip);
        await StoreAsync(workflow);

        (await FireAsync(Utc(2026, 10, 19, 12, 30))).Should().Be(0, "10:00, 11:00 and 12:00 are older than the on-time window");

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var claims = await session.Query<ScheduleClaim>().Where(c => c.WorkflowId == workflow.Id).ToListAsync(Ct);

        claims.Should().ContainSingle("the skipped window is one claim, not one per time");
        claims[0].DueAt.Should().Be(Utc(2026, 10, 19, 12, 0), "the next sweep counts from the newest skipped time");
        (await RunsOfAsync(workflow.Id)).Should().BeEmpty("a skipped time starts no run");

        (await FireAsync(Utc(2026, 10, 19, 13, 0, 20))).Should().Be(1);
    }

    // Once a year, so a workflow armed two years back has two missed times (January 1 of last year and
    // of this one) and none within the seconds a test takes.
    private const string Yearly = "0 0 1 1 *";

    [Fact]
    public async Task A_schedule_disabled_and_enabled_again_by_the_update_endpoint_arms_from_now_and_catches_up_nothing()
    {
        var workflow = Scheduled(Yearly, "UTC", new DateTime(DateTime.UtcNow.Year - 2, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            WorkflowSchedule.CatchUpEach, enabled: false);
        await StoreAsync(workflow);

        var (token, _) = await TestHelpers.CreateAdminUserAsync(_fixture);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var before = DateTime.UtcNow;
        var response = await client.PutAsJsonAsync($"/api/workflows/{workflow.Id}", new
        {
            name = workflow.Name,
            triggerContentType = "Article",
            triggerContentTypes = new[] { "Article" },
            triggerEvent = WorkflowEvents.Schedule,
            triggerEvents = new[] { WorkflowEvents.Schedule },
            schedule = Yearly,
            scheduleTimeZone = "UTC",
            scheduleCatchUp = WorkflowSchedule.CatchUpEach,
            enabled = true,
            actions = new[] { new { type = "Email", parameters = new Dictionary<string, string> { ["To"] = "a@b.com", ["Subject"] = "s", ["Body"] = "b" } } },
        }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await session.LoadAsync<WorkflowDefinition>(workflow.Id, Ct);

        stored!.Enabled.Should().BeTrue();
        stored.ScheduleArmedAt.Should().BeOnOrAfter(before, "the stored time passed while the schedule was off, so it arms from now");
        WorkflowSchedule.Next(WorkflowSchedule.Parse(Yearly)!, TimeZoneInfo.Utc, stored.ScheduleArmedAt!.Value, 1)
            .Single().Should().BeAfter(DateTime.UtcNow, "the next time is ahead of now");

        (await FireAsync(DateTime.UtcNow)).Should().Be(0, "the January 1 times that passed while it was off are not owed");
        (await RunsOfAsync(workflow.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_schedule_disabled_and_enabled_again_by_config_apply_arms_from_now_and_catches_up_nothing()
    {
        var callers = new ConfigCallers(_fixture);
        var tenant = await callers.TenantAsync();
        var client = await callers.SuperAdminAsync(tenant);
        var name = "rearm-" + Guid.NewGuid().ToString("N")[..10];
        await callers.StoreInTenantAsync(tenant, new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = "delivery", DisplayName = "Delivery",
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
        });

        object Document(bool enabled) => new
        {
            version = 1,
            workflows = new Dictionary<string, object>
            {
                [name] = new
                {
                    triggerContentTypes = new[] { "delivery" },
                    triggerEvents = new[] { WorkflowEvents.Schedule },
                    schedule = Yearly,
                    scheduleTimeZone = "UTC",
                    scheduleCatchUp = WorkflowSchedule.CatchUpEach,
                    enabled,
                    actions = new[] { new { type = "Webhook", parameters = new Dictionary<string, string> { ["Url"] = "https://hooks.example.com/a" } } },
                },
            },
        };

        var (created, createdBody) = await ConfigCallers.ApplyAsync(client, Document(false), allowDestructive: true);
        created.StatusCode.Should().Be(HttpStatusCode.OK, createdBody.ToString());

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        Guid id;
        await using (var write = store.LightweightSession(tenant))
        {
            var stored = await write.Query<WorkflowDefinition>().SingleAsync(w => w.Name == name, Ct);
            id = stored.Id;
            stored.ScheduleArmedAt = new DateTime(DateTime.UtcNow.Year - 2, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            write.Store(stored);
            await write.SaveChangesAsync(Ct);
        }

        var before = DateTime.UtcNow;
        var (enabled, enabledBody) = await ConfigCallers.ApplyAsync(client, Document(true), allowDestructive: true);
        enabled.StatusCode.Should().Be(HttpStatusCode.OK, enabledBody.ToString());

        await using var read = store.LightweightSession(tenant);
        var after = await read.LoadAsync<WorkflowDefinition>(id, Ct);
        after!.Enabled.Should().BeTrue();
        after.ScheduleArmedAt.Should().BeOnOrAfter(before, "the stored time passed while the schedule was off, so apply arms from now");
        WorkflowSchedule.Next(WorkflowSchedule.Parse(Yearly)!, TimeZoneInfo.Utc, after.ScheduleArmedAt!.Value, 1)
            .Single().Should().BeAfter(DateTime.UtcNow, "the next time is ahead of now");

        await WorkflowSchedule.FireDueAsync(read, DateTime.UtcNow, null, Ct);
        (await read.Query<WorkflowRun>().CountAsync(r => r.WorkflowDefinitionId == id, Ct))
            .Should().Be(0, "the January 1 times that passed while it was off are not owed");
    }

    [Theory]
    [InlineData(false, true, "0 * * * *", "0 * * * *", true, "off then on re-arms")]
    [InlineData(true, true, "0 * * * *", "0 * * * *", false, "on and still on keeps the stored time")]
    [InlineData(false, false, "0 * * * *", "0 * * * *", false, "off and still off keeps the stored time")]
    [InlineData(true, true, "0 * * * *", "30 * * * *", true, "a changed schedule re-arms")]
    public void ArmedAt_re_arms_when_a_workflow_is_enabled_or_its_schedule_changes(
        bool wasEnabled, bool isEnabled, string was, string now, bool rearmed, string why)
    {
        var armed = Utc(2026, 1, 1, 0, 0);
        var stored = Scheduled(was, "UTC", armed, enabled: wasEnabled);
        var next = Scheduled(now, "UTC", armed, enabled: isEnabled);
        var clock = Utc(2026, 10, 19, 12, 0);

        WorkflowSchedule.ArmedAt(stored, next, clock).Should().Be(rearmed ? clock : armed, why);
    }
}
