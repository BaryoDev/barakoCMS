using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using BarakoCMS.Scheduling;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class SchedulingTests
{
    private readonly IntegrationTestFixture _factory;

    public SchedulingTests(IntegrationTestFixture factory) => _factory = factory;

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Recorder(string action, bool throws = false) : IScheduledActionHandler
    {
        public List<ScheduledActionContext> Calls { get; } = [];

        public string Action => action;

        public Task HandleAsync(ScheduledActionContext context, CancellationToken ct)
        {
            Calls.Add(context);
            if (throws)
                throw new InvalidOperationException("handler crashed");
            return Task.CompletedTask;
        }
    }

    private IDocumentStore Store => _factory.Services.GetRequiredService<IDocumentStore>();

    private static string NewAction() => $"test-{Guid.NewGuid():N}";

    private ScheduleSweepService Sweeper(TimeProvider time, params IScheduledActionHandler[] handlers) =>
        new(Store, handlers, time, NullLogger<ScheduleSweepService>.Instance);

    private Task<Guid> ScheduleAsync(string? tenant, string action, DateTimeOffset due) =>
        new Scheduler(Store).ScheduleAsync(tenant, action, due, "{\"n\":1}", TestContext.Current.CancellationToken);

    private async Task<ScheduledEntry> LoadAsync(Guid id)
    {
        await using var session = Store.QuerySession();
        var entry = await session.LoadAsync<ScheduledEntry>(id, TestContext.Current.CancellationToken);
        entry.Should().NotBeNull();
        return entry!;
    }

    [Fact]
    public async Task An_entry_is_not_fired_before_it_is_due()
    {
        var start = DateTimeOffset.UtcNow;
        var time = new ManualTime(start);
        var action = NewAction();
        var handler = new Recorder(action);
        var id = await ScheduleAsync(null, action, start.AddMinutes(10));

        var fired = await Sweeper(time, handler).SweepAsync(TestContext.Current.CancellationToken);

        fired.Should().Be(0);
        handler.Calls.Should().BeEmpty();
        (await LoadAsync(id)).Status.Should().Be(ScheduledEntryStatus.Pending);
    }

    [Fact]
    public async Task An_entry_is_fired_once_its_due_time_arrives()
    {
        // Whole seconds: Postgres keeps microseconds and rounds a stored 100ns tick up, which puts the
        // due time a hair past a clock advanced by exactly ten minutes.
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var time = new ManualTime(start);
        var action = NewAction();
        var handler = new Recorder(action);
        var id = await ScheduleAsync(null, action, start.AddMinutes(10));
        var sweeper = Sweeper(time, handler);

        await sweeper.SweepAsync(TestContext.Current.CancellationToken);
        handler.Calls.Should().BeEmpty();

        time.Advance(TimeSpan.FromMinutes(10));
        var fired = await sweeper.SweepAsync(TestContext.Current.CancellationToken);

        fired.Should().Be(1);
        handler.Calls.Should().HaveCount(1);
        handler.Calls[0].EntryId.Should().Be(id);
        handler.Calls[0].Payload.Should().Be("{\"n\":1}");
        var entry = await LoadAsync(id);
        entry.Status.Should().Be(ScheduledEntryStatus.Fired);
        entry.FiredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_second_sweep_does_not_fire_a_fired_entry_again()
    {
        var start = DateTimeOffset.UtcNow;
        var time = new ManualTime(start);
        var action = NewAction();
        var handler = new Recorder(action);
        await ScheduleAsync(null, action, start.AddMinutes(1));
        time.Advance(TimeSpan.FromMinutes(5));

        await Sweeper(time, handler).SweepAsync(TestContext.Current.CancellationToken);
        await Sweeper(time, handler).SweepAsync(TestContext.Current.CancellationToken);

        handler.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task Two_instances_sweeping_together_fire_an_entry_once()
    {
        var start = DateTimeOffset.UtcNow;
        var time = new ManualTime(start);
        var action = NewAction();
        var handler = new Recorder(action);
        await ScheduleAsync(null, action, start.AddMinutes(1));
        time.Advance(TimeSpan.FromMinutes(5));

        await Task.WhenAll(
            Sweeper(time, handler).SweepAsync(TestContext.Current.CancellationToken),
            Sweeper(time, handler).SweepAsync(TestContext.Current.CancellationToken));

        handler.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_handler_crash_marks_the_entry_failed_and_a_retry_does_not_fire_it_again()
    {
        var start = DateTimeOffset.UtcNow;
        var time = new ManualTime(start);
        var action = NewAction();
        var handler = new Recorder(action, throws: true);
        var id = await ScheduleAsync(null, action, start.AddMinutes(1));
        time.Advance(TimeSpan.FromMinutes(5));
        var sweeper = Sweeper(time, handler);

        await sweeper.SweepAsync(TestContext.Current.CancellationToken);
        await sweeper.SweepAsync(TestContext.Current.CancellationToken);

        handler.Calls.Should().HaveCount(1);
        var entry = await LoadAsync(id);
        entry.Status.Should().Be(ScheduledEntryStatus.Failed);
        entry.Error.Should().Be("handler crashed");
    }

    [Fact]
    public async Task An_entry_goes_only_to_the_handler_for_its_action_with_its_own_tenant()
    {
        var start = DateTimeOffset.UtcNow;
        var time = new ManualTime(start);
        var actionA = NewAction();
        var actionB = NewAction();
        var handlerA = new Recorder(actionA);
        var handlerB = new Recorder(actionB);
        await ScheduleAsync("club-a", actionA, start.AddMinutes(1));
        await ScheduleAsync("club-b", actionB, start.AddMinutes(1));
        time.Advance(TimeSpan.FromMinutes(5));

        await Sweeper(time, handlerA, handlerB).SweepAsync(TestContext.Current.CancellationToken);

        handlerA.Calls.Should().HaveCount(1);
        handlerA.Calls[0].TenantId.Should().Be("club-a");
        handlerB.Calls.Should().HaveCount(1);
        handlerB.Calls[0].TenantId.Should().Be("club-b");
    }

    [Fact]
    public async Task A_cancelled_entry_is_never_fired()
    {
        var start = DateTimeOffset.UtcNow;
        var time = new ManualTime(start);
        var action = NewAction();
        var handler = new Recorder(action);
        var scheduler = new Scheduler(Store);
        var id = await ScheduleAsync(null, action, start.AddMinutes(1));

        (await scheduler.CancelAsync(id, TestContext.Current.CancellationToken)).Should().BeTrue();
        time.Advance(TimeSpan.FromMinutes(5));
        await Sweeper(time, handler).SweepAsync(TestContext.Current.CancellationToken);

        handler.Calls.Should().BeEmpty();
        (await LoadAsync(id)).Status.Should().Be(ScheduledEntryStatus.Cancelled);
        (await scheduler.CancelAsync(id, TestContext.Current.CancellationToken)).Should().BeFalse();
    }
}
