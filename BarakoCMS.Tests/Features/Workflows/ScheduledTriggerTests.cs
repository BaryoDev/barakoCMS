using System.Globalization;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// A schedule workflow queues one run for each value of its date field, once the field plus the
/// offset has passed, and never for a value that is missing, empty or not a date.
/// </summary>
/// <remarks>Entries and workflows are made under a content type of the test's own, so one test's sweep queues nothing for another's.</remarks>
[Collection("Integration4")]
public class ScheduledTriggerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTime Now = new(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc);

    private readonly WorkflowStopHarness _harness;

    public ScheduledTriggerTests(IntegrationTestFixture factory) => _harness = new WorkflowStopHarness(factory);

    [Fact]
    public async Task A_due_entry_queues_one_run_with_a_key_from_the_workflow_entry_and_value()
    {
        var (type, workflow) = await TypeAndWorkflowAsync(offset: "-24h");
        var startsAt = Now.AddHours(20);
        var entry = await StoreEntryAsync(type, Text(startsAt));

        (await SweepAsync(Now)).Should().Be(1);

        var run = (await RunsAsync(workflow)).Should().ContainSingle().Subject;
        run.ContentId.Should().Be(entry);
        run.TriggerKey.Should().Be($"{workflow:N}:{entry:N}:{startsAt.ToString("O", CultureInfo.InvariantCulture)}");
        run.TriggerEvent.Should().StartWith("schedule:");
    }

    [Fact]
    public async Task Sweeps_that_see_one_value_at_once_queue_one_run()
    {
        var (type, workflow) = await TypeAndWorkflowAsync(offset: "-24h");
        await StoreEntryAsync(type, Text(Now.AddHours(20)));

        var counts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SweepAsync(Now)));

        counts.Sum().Should().Be(1);
        (await RunsAsync(workflow)).Should().ContainSingle();
    }

    [Fact]
    public async Task The_grace_is_a_setting()
    {
        var (type, workflow) = await TypeAndWorkflowAsync(offset: "-24h");
        // Fire time is 4 days before Now: inside the default 7 days, outside a 2 day setting.
        await StoreEntryAsync(type, Text(Now.AddDays(-3)));

        (await SweepAsync(Now, Settings((ScheduleSweep.GraceDaysKey, "2")))).Should().Be(0);
        (await SweepAsync(Now)).Should().Be(1);
        (await RunsAsync(workflow)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_small_page_size_still_sweeps_every_entry()
    {
        var (type, workflow) = await TypeAndWorkflowAsync(offset: "-24h");
        for (var i = 0; i < 5; i++) await StoreEntryAsync(type, Text(Now.AddHours(20)));

        (await SweepAsync(Now, Settings((ScheduleSweep.PageSizeKey, "2")))).Should().Be(5);
        (await RunsAsync(workflow)).Should().HaveCount(5);
    }

    [Fact]
    public async Task A_second_sweep_for_the_same_value_queues_nothing()
    {
        var (type, workflow) = await TypeAndWorkflowAsync(offset: "-24h");
        await StoreEntryAsync(type, Text(Now.AddHours(20)));

        await SweepAsync(Now);
        (await SweepAsync(Now.AddMinutes(1))).Should().Be(0);

        (await RunsAsync(workflow)).Should().ContainSingle();
    }

    [Fact]
    public async Task An_entry_moved_to_a_new_due_date_queues_again()
    {
        var (type, workflow) = await TypeAndWorkflowAsync(offset: "-24h");
        var entry = await StoreEntryAsync(type, Text(Now.AddHours(20)));
        await SweepAsync(Now);

        await SetDateAsync(entry, Text(Now.AddHours(22)));
        (await SweepAsync(Now.AddMinutes(1))).Should().Be(1);

        (await RunsAsync(workflow)).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_entry_moved_back_to_the_value_that_fired_does_not_fire_again()
    {
        var (type, workflow) = await TypeAndWorkflowAsync(offset: "-24h");
        var first = Text(Now.AddHours(20));
        var entry = await StoreEntryAsync(type, first);
        await SweepAsync(Now);
        await SetDateAsync(entry, Text(Now.AddDays(10)));
        await SweepAsync(Now.AddMinutes(1));

        await SetDateAsync(entry, first);
        (await SweepAsync(Now.AddMinutes(2))).Should().Be(0);

        (await RunsAsync(workflow)).Should().ContainSingle();
    }

    [Fact]
    public async Task An_entry_not_yet_due_queues_nothing()
    {
        var (type, workflow) = await TypeAndWorkflowAsync(offset: "-24h");
        await StoreEntryAsync(type, Text(Now.AddHours(30)));

        (await SweepAsync(Now)).Should().Be(0);

        (await RunsAsync(workflow)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_entry_past_the_grace_after_its_fire_time_queues_nothing()
    {
        var (type, workflow) = await TypeAndWorkflowAsync(offset: "-24h");
        // Fire time is a day before the value, so a value 6 days back fired 7 days back.
        await StoreEntryAsync(type, Text(Now.AddDays(-6).AddHours(-1)));
        var inside = await StoreEntryAsync(type, Text(Now.AddDays(-5)));

        (await SweepAsync(Now)).Should().Be(1);

        (await RunsAsync(workflow)).Should().ContainSingle().Which.ContentId.Should().Be(inside);
    }

    [Fact]
    public async Task An_entry_in_another_state_queues_nothing()
    {
        var (type, workflow) = await TypeAndWorkflowAsync(offset: "-24h", inState: "Booked");
        await StoreEntryAsync(type, Text(Now.AddHours(20)), state: "Cancelled");
        var booked = await StoreEntryAsync(type, Text(Now.AddHours(20)), state: "Booked");

        await SweepAsync(Now);

        (await RunsAsync(workflow)).Should().ContainSingle().Which.ContentId.Should().Be(booked);
    }

    [Fact]
    public async Task An_entry_that_does_not_match_where_queues_nothing()
    {
        var (type, workflow) = await TypeAndWorkflowAsync(offset: "-24h", where: new() { ["Type"] = "Walk-in" });
        await StoreEntryAsync(type, Text(Now.AddHours(20)), extra: ("Type", "Booked-ahead"));
        var match = await StoreEntryAsync(type, Text(Now.AddHours(20)), extra: ("Type", "Walk-in"));

        await SweepAsync(Now);

        (await RunsAsync(workflow)).Should().ContainSingle().Which.ContentId.Should().Be(match);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("2026-02-30T09:00:00Z")]
    [InlineData("2026-13-01T09:00:00Z")]
    public async Task A_value_that_is_not_a_date_queues_nothing_and_does_not_throw(string? value)
    {
        var (type, workflow) = await TypeAndWorkflowAsync(offset: "-24h");
        await StoreEntryAsync(type, value);
        var good = await StoreEntryAsync(type, Text(Now.AddHours(20)));

        var queued = await SweepAsync(Now);

        queued.Should().Be(1);
        (await RunsAsync(workflow)).Should().ContainSingle().Which.ContentId.Should().Be(good);
    }

    [Fact]
    public async Task A_disabled_workflow_queues_nothing()
    {
        var (type, workflow) = await TypeAndWorkflowAsync(offset: "-24h", enabled: false);
        await StoreEntryAsync(type, Text(Now.AddHours(20)));

        (await SweepAsync(Now)).Should().Be(0);

        (await RunsAsync(workflow)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_queued_run_executes_the_workflows_action()
    {
        var (type, workflow) = await TypeAndWorkflowAsync(offset: "-24h");
        await StoreEntryAsync(type, Text(Now.AddHours(20)));
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await SweepAsync(Now);
            await _harness.DrainAsync();
            run = (await RunsAsync(workflow)).Single();
        });

        run.Actions.Should().ContainSingle().Which.Status.Should().Be(AttemptStatus.Succeeded);
        WorkflowStopHarness.TimesRun(run.Actions[0]).Should().Be(1);
    }

    private static string Text(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);

    private async Task<(string Type, Guid Workflow)> TypeAndWorkflowAsync(
        string offset, string? inState = null, Dictionary<string, string>? where = null, bool enabled = true)
    {
        var type = WorkflowStopHarness.NewName("appointment");
        var workflow = await _harness.StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = type;
            w.TriggerContentTypes = [type];
            w.TriggerEvent = "schedule:" + w.Name;
            w.Enabled = enabled;
            w.DateSchedule = new ScheduleTrigger
            {
                ContentType = type, DateField = "startsAt", Offset = offset, InState = inState, Where = where ?? new(),
            };
        });
        return (type, workflow);
    }

    private async Task<Guid> StoreEntryAsync(string type, string? startsAt, string? state = null, (string, string)? extra = null)
    {
        var data = new Dictionary<string, object>();
        if (startsAt is not null) data["startsAt"] = startsAt;
        if (extra is { } pair) data[pair.Item1] = pair.Item2;

        var entry = new Content
        {
            Id = Guid.CreateVersion7(), ContentType = type, Status = ContentStatus.Published, LifecycleState = state, Data = data,
        };
        await using var session = _harness.Store.LightweightSession();
        session.Store(entry);
        await session.SaveChangesAsync(Ct);
        return entry.Id;
    }

    private async Task SetDateAsync(Guid entry, string startsAt)
    {
        await using var session = _harness.Store.LightweightSession();
        var content = await session.LoadAsync<Content>(entry, Ct);
        content!.Data["startsAt"] = startsAt;
        session.Store(content);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<int> SweepAsync(DateTime now, Microsoft.Extensions.Configuration.IConfiguration? config = null)
    {
        await using var session = _harness.Store.LightweightSession();
        var queue = new WorkflowRunQueue(session, NullLogger<WorkflowRunQueue>.Instance);
        return await ScheduleSweep.RunAsync(session, queue, now, Ct, config);
    }

    private static Microsoft.Extensions.Configuration.IConfiguration Settings(params (string Key, string Value)[] pairs) =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    private async Task<List<WorkflowRun>> RunsAsync(Guid workflow)
    {
        await using var session = _harness.Store.QuerySession();
        return (await session.Query<WorkflowRun>().Where(r => r.WorkflowDefinitionId == workflow).ToListAsync(Ct)).ToList();
    }
}
