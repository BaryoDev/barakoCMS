using barakoCMS.Core.Interfaces;
using FluentAssertions;

namespace BarakoCMS.Tests;

/// <summary>
/// What the durable work seams promise, shown against the in-memory fake: the transactional
/// property first, then the tenant, the delay, once per key, wait and resume, and retry.
/// </summary>
/// <remarks>
/// No container and no clock. Every test starts at <see cref="Start"/> and moves time itself.
/// </remarks>
public class InMemoryDurableWorkTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Ping(string Text);

    public sealed record Tick(DateTimeOffset DueAt);

    public sealed record Reply(string Answer);

    public sealed record TimedOut(string WaitKey);

    public sealed class Counter
    {
        public int Value { get; set; }
    }

    private static InMemoryDurableWork Work() =>
        new InMemoryDurableWork(Start)
            .Handle<Ping>((_, _, _, _) => Task.CompletedTask)
            .Handle<Reply>((_, _, _, _) => Task.CompletedTask)
            .Handle<TimedOut>((_, _, _, _) => Task.CompletedTask);

    private static async Task CommitAsync(InMemoryDurableWork work, string tenant, Func<InMemoryUnitOfWork, Task> stage)
    {
        using var unit = work.Begin(tenant);
        await stage(unit);
        unit.Commit();
    }

    [Fact]
    public async Task A_message_is_handled_only_after_its_unit_of_work_commits()
    {
        var work = Work();

        using (var unit = work.Begin("alpha"))
        {
            await unit.EnqueueAsync("alpha", new Ping("committed"), Ct);

            (await work.RunDueAsync(Ct)).Should().Be(0, "nothing is stored until the unit of work commits");
            work.Pending.Should().BeEmpty();

            unit.Commit();
        }

        (await work.RunDueAsync(Ct)).Should().Be(1);
        work.Handled<Ping>().Should().ContainSingle().Which.Text.Should().Be("committed");
    }

    [Fact]
    public async Task A_message_staged_in_a_unit_of_work_that_is_never_committed_is_never_handled()
    {
        var work = Work();

        using (var abandoned = work.Begin("alpha"))
        {
            await abandoned.EnqueueAsync("alpha", new Ping("abandoned"), Ct);
            await abandoned.ScheduleAsync("alpha", new Ping("abandoned later"), Start.AddMinutes(5), Ct);
            await abandoned.StartAsync("alpha", "run-abandoned", new Ping("abandoned run"), Ct);
            await abandoned.WaitAsync("alpha", "wait-abandoned", Start.AddMinutes(5), new TimedOut("wait-abandoned"), Ct);
        }

        // The control. Without it, a fake that delivered nothing at all would pass.
        await CommitAsync(work, "alpha", unit => unit.EnqueueAsync("alpha", new Ping("kept"), Ct));

        await work.AdvanceAsync(TimeSpan.FromHours(1), Ct);

        work.Delivered.Should().HaveCount(1);
        work.Handled<Ping>().Should().ContainSingle().Which.Text.Should().Be("kept");
        work.Waits.Should().BeEmpty();
        work.Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task A_unit_of_work_whose_commit_fails_leaves_nothing_behind()
    {
        var work = Work();
        await CommitAsync(work, "alpha", unit =>
            unit.WaitAsync("alpha", "reply-1", Start.AddHours(1), new TimedOut("reply-1"), Ct));

        using var first = work.Begin("alpha");
        using var second = work.Begin("alpha");

        (await first.ResumeAsync("alpha", "reply-1", new Reply("first"), Ct)).Should().BeTrue();
        (await second.ResumeAsync("alpha", "reply-1", new Reply("second"), Ct)).Should().BeTrue(
            "neither resume has committed, so both still see the wait");
        await second.EnqueueAsync("alpha", new Ping("staged beside the losing resume"), Ct);

        first.Commit();

        var commit = () => second.Commit();
        commit.Should().Throw<InvalidOperationException>();
        second.Committed.Should().BeFalse();

        await work.AdvanceAsync(TimeSpan.FromHours(2), Ct);

        work.Delivered.Should().HaveCount(1);
        work.Handled<Reply>().Should().ContainSingle().Which.Answer.Should().Be("first");
        work.Handled<Ping>().Should().BeEmpty("a message staged in a unit of work that failed to commit must not outlive it");
        work.Handled<TimedOut>().Should().BeEmpty("the wait was resumed, so it does not also time out");
    }

    [Fact]
    public async Task A_handler_is_told_the_tenant_the_caller_named()
    {
        var contexts = new List<DurableMessageContext>();
        var work = new InMemoryDurableWork(Start).Handle<Ping>((_, context, _, _) =>
        {
            contexts.Add(context);
            return Task.CompletedTask;
        });

        await CommitAsync(work, "alpha", unit => unit.EnqueueAsync("alpha", new Ping("for alpha"), Ct));
        await CommitAsync(work, "beta", unit => unit.ScheduleAsync("beta", new Ping("for beta"), Start, Ct));

        await work.RunDueAsync(Ct);

        contexts.Should().HaveCount(2);
        contexts.Select(c => c.Tenant).Should().Equal("alpha", "beta");
        contexts.Select(c => c.MessageId).Should().OnlyHaveUniqueItems();
        work.Delivered.Select(d => d.Tenant).Should().Equal("alpha", "beta");
    }

    [Fact]
    public async Task A_handler_is_given_a_unit_of_work_for_the_tenant_of_its_message()
    {
        var tenants = new List<string>();
        var work = new InMemoryDurableWork(Start).Handle<Ping>((_, _, unit, _) =>
        {
            tenants.Add(unit.Tenant);
            return Task.CompletedTask;
        });

        await CommitAsync(work, "beta", unit => unit.EnqueueAsync("beta", new Ping("for beta"), Ct));
        await work.RunDueAsync(Ct);

        tenants.Should().ContainSingle().Which.Should().Be("beta");
    }

    [Fact]
    public async Task A_unit_of_work_refuses_work_named_for_another_tenant()
    {
        var work = Work();
        await CommitAsync(work, "beta", unit =>
            unit.WaitAsync("beta", "reply-1", Start.AddHours(1), new TimedOut("reply-1"), Ct));

        using var unit = work.Begin("alpha");

        var calls = new Func<Task>[]
        {
            async () => await unit.EnqueueAsync("beta", new Ping("x"), Ct),
            async () => await unit.ScheduleAsync("beta", new Ping("x"), Start, Ct),
            async () => await unit.StartAsync("beta", "run-1", new Ping("x"), Ct),
            async () => await unit.WaitAsync("beta", "wait-1", Start, new TimedOut("wait-1"), Ct),
            async () => await unit.ResumeAsync("beta", "reply-1", new Reply("x"), Ct),
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<ArgumentException>();
        }

        unit.Commit();
        (await work.RunDueAsync(Ct)).Should().Be(0, "a refused call stages nothing");
        work.Waits.Should().ContainSingle().Which.WaitKey.Should().Be("reply-1");
    }

    [Fact]
    public async Task What_a_handler_stages_is_kept_only_if_the_handler_commits()
    {
        var work = new InMemoryDurableWork(Start)
            .Handle<Reply>((_, _, _, _) => Task.CompletedTask)
            .Handle<Ping>(async (ping, context, unit, ct) =>
            {
                await unit.EnqueueAsync(context.Tenant, new Reply(ping.Text), ct);

                if (ping.Text == "saves")
                {
                    unit.Commit();
                }
            });

        await CommitAsync(work, "alpha", async unit =>
        {
            await unit.EnqueueAsync("alpha", new Ping("saves"), Ct);
            await unit.EnqueueAsync("alpha", new Ping("forgets to save"), Ct);
        });

        await work.RunDueAsync(Ct);

        work.Handled<Ping>().Should().HaveCount(2);
        work.Handled<Reply>().Should().ContainSingle().Which.Answer.Should().Be("saves");
    }

    [Fact]
    public async Task A_scheduled_message_is_not_handled_before_its_time()
    {
        var work = Work();
        var dueAt = Start.AddMinutes(10);
        await CommitAsync(work, "alpha", unit => unit.ScheduleAsync("alpha", new Ping("later"), dueAt, Ct));

        (await work.AdvanceToAsync(dueAt - TimeSpan.FromTicks(1), Ct)).Should().Be(0);
        work.Pending.Should().ContainSingle().Which.DueAt.Should().Be(dueAt);

        (await work.AdvanceAsync(TimeSpan.FromTicks(1), Ct)).Should().Be(1);
        work.Delivered.Should().ContainSingle().Which.At.Should().Be(dueAt);
    }

    [Fact]
    public async Task The_clock_stands_at_each_due_time_while_its_message_is_handled()
    {
        var work = Work();
        await CommitAsync(work, "alpha", async unit =>
        {
            await unit.ScheduleAsync("alpha", new Ping("second"), Start.AddMinutes(20), Ct);
            await unit.ScheduleAsync("alpha", new Ping("first"), Start.AddMinutes(10), Ct);
            await unit.EnqueueAsync("alpha", new Ping("now"), Ct);
            await unit.ScheduleAsync("alpha", new Ping("already past"), Start.AddMinutes(-5), Ct);
        });

        (await work.AdvanceAsync(TimeSpan.FromHours(1), Ct)).Should().Be(4);

        work.Handled<Ping>().Select(p => p.Text).Should().Equal("already past", "now", "first", "second");
        work.Delivered.Select(d => d.At).Should().Equal(Start, Start, Start.AddMinutes(10), Start.AddMinutes(20));
        work.Now.Should().Be(Start.AddHours(1));
    }

    [Fact]
    public async Task A_run_id_starts_once_per_tenant()
    {
        var work = Work();

        using (var one = work.Begin("alpha"))
        using (var two = work.Begin("alpha"))
        {
            await one.StartAsync("alpha", "run-1", new Ping("first"), Ct);
            await two.StartAsync("alpha", "run-1", new Ping("raced"), Ct);
            one.Commit();
            two.Commit();
        }

        await work.RunDueAsync(Ct);

        await CommitAsync(work, "alpha", unit => unit.StartAsync("alpha", "run-1", new Ping("after it ran"), Ct));
        await CommitAsync(work, "beta", unit => unit.StartAsync("beta", "run-1", new Ping("another tenant"), Ct));
        await CommitAsync(work, "alpha", unit => unit.StartAsync("alpha", "run-2", new Ping("another run"), Ct));

        await work.RunDueAsync(Ct);

        work.Handled<Ping>().Should().HaveCount(3);
        work.Handled<Ping>().Select(p => p.Text).Should().Equal("first", "another tenant", "another run");
    }

    [Fact]
    public async Task A_resumed_wait_delivers_the_resume_once_and_never_its_timeout()
    {
        var work = Work();
        await CommitAsync(work, "alpha", unit =>
            unit.WaitAsync("alpha", "reply-1", Start.AddHours(1), new TimedOut("reply-1"), Ct));

        work.Waits.Should().ContainSingle().Which.TimeoutAt.Should().Be(Start.AddHours(1));
        (await work.AdvanceAsync(TimeSpan.FromMinutes(30), Ct)).Should().Be(0, "a parked run does nothing while it waits");

        using (var unit = work.Begin("alpha"))
        {
            (await unit.ResumeAsync("alpha", "reply-1", new Reply("answered"), Ct)).Should().BeTrue();
            (await unit.ResumeAsync("alpha", "reply-1", new Reply("answered twice"), Ct)).Should().BeFalse();
            unit.Commit();
        }

        using (var late = work.Begin("alpha"))
        {
            (await late.ResumeAsync("alpha", "reply-1", new Reply("answered again"), Ct)).Should().BeFalse();
            late.Commit();
        }

        await work.AdvanceAsync(TimeSpan.FromHours(2), Ct);

        work.Delivered.Should().HaveCount(1);
        work.Handled<Reply>().Should().ContainSingle().Which.Answer.Should().Be("answered");
        work.Delivered[0].At.Should().Be(Start.AddMinutes(30));
        work.Handled<TimedOut>().Should().BeEmpty();
        work.Waits.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wait_nobody_resumes_delivers_its_timeout_and_a_late_resume_finds_nothing()
    {
        var work = Work();
        await CommitAsync(work, "alpha", unit =>
            unit.WaitAsync("alpha", "reply-1", Start.AddHours(1), new TimedOut("reply-1"), Ct));

        (await work.AdvanceToAsync(Start.AddHours(1) - TimeSpan.FromTicks(1), Ct)).Should().Be(0);
        (await work.AdvanceAsync(TimeSpan.FromTicks(1), Ct)).Should().Be(1);

        using (var late = work.Begin("alpha"))
        {
            (await late.ResumeAsync("alpha", "reply-1", new Reply("too late"), Ct)).Should().BeFalse();
            late.Commit();
        }

        await work.RunDueAsync(Ct);

        work.Delivered.Should().HaveCount(1);
        work.Handled<TimedOut>().Should().ContainSingle().Which.WaitKey.Should().Be("reply-1");
        work.Handled<Reply>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_resume_staged_before_the_timeout_and_committed_after_it_fails_to_commit()
    {
        var work = Work();
        await CommitAsync(work, "alpha", unit =>
            unit.WaitAsync("alpha", "reply-1", Start.AddHours(1), new TimedOut("reply-1"), Ct));

        using var slow = work.Begin("alpha");
        (await slow.ResumeAsync("alpha", "reply-1", new Reply("slow"), Ct)).Should().BeTrue();

        await work.AdvanceAsync(TimeSpan.FromHours(1), Ct);

        var commit = () => slow.Commit();
        commit.Should().Throw<InvalidOperationException>();

        await work.RunDueAsync(Ct);

        work.Delivered.Should().HaveCount(1);
        work.Handled<TimedOut>().Should().ContainSingle();
        work.Handled<Reply>().Should().BeEmpty("the timeout won, so the wait is not also resumed");
    }

    [Fact]
    public async Task A_wait_is_pending_only_once_committed_and_only_in_its_own_tenant()
    {
        var work = Work();

        using (var unit = work.Begin("alpha"))
        {
            await unit.WaitAsync("alpha", "reply-1", Start.AddHours(1), new TimedOut("reply-1"), Ct);

            (await unit.ResumeAsync("alpha", "reply-1", new Reply("too early"), Ct)).Should().BeFalse(
                "the wait is staged and has not committed");

            unit.Commit();
        }

        using (var other = work.Begin("beta"))
        {
            (await other.ResumeAsync("beta", "reply-1", new Reply("wrong tenant"), Ct)).Should().BeFalse();
            other.Commit();
        }

        await work.RunDueAsync(Ct);
        work.Delivered.Should().BeEmpty();
        work.Waits.Should().ContainSingle().Which.Tenant.Should().Be("alpha");

        using (var own = work.Begin("alpha"))
        {
            (await own.ResumeAsync("alpha", "reply-1", new Reply("right tenant"), Ct)).Should().BeTrue();
            own.Commit();
        }

        await work.RunDueAsync(Ct);
        work.Handled<Reply>().Should().ContainSingle().Which.Answer.Should().Be("right tenant");
    }

    [Fact]
    public async Task A_wait_key_that_was_already_used_does_not_park_again()
    {
        var work = Work();
        await CommitAsync(work, "alpha", unit =>
            unit.WaitAsync("alpha", "reply-1", Start.AddHours(1), new TimedOut("reply-1"), Ct));
        await CommitAsync(work, "alpha", async unit =>
            (await unit.ResumeAsync("alpha", "reply-1", new Reply("answered"), Ct)).Should().BeTrue());
        await work.RunDueAsync(Ct);

        // The handler that parked the run is run again, as after a crash, and parks it again.
        await CommitAsync(work, "alpha", unit =>
            unit.WaitAsync("alpha", "reply-1", Start.AddHours(1), new TimedOut("reply-1"), Ct));

        // The control: a key that was never used does park.
        await CommitAsync(work, "alpha", unit =>
            unit.WaitAsync("alpha", "reply-2", Start.AddHours(1), new TimedOut("reply-2"), Ct));

        work.Waits.Should().ContainSingle().Which.WaitKey.Should().Be("reply-2");

        await work.AdvanceAsync(TimeSpan.FromHours(2), Ct);

        work.Handled<TimedOut>().Should().ContainSingle().Which.WaitKey.Should().Be("reply-2");
        work.Handled<Reply>().Should().ContainSingle().Which.Answer.Should().Be("answered");
    }

    [Fact]
    public async Task A_run_that_parks_itself_until_its_next_due_time_fires_once_per_due_time()
    {
        var interval = TimeSpan.FromHours(1);
        var work = new InMemoryDurableWork(Start).Handle<Tick>(async (tick, context, unit, ct) =>
        {
            var next = tick.DueAt + interval;
            await unit.WaitAsync(context.Tenant, $"sweep:{next:O}", next, new Tick(next), ct);
            unit.Commit();
        });

        await CommitAsync(work, "alpha", unit => unit.StartAsync("alpha", $"sweep:{Start:O}", new Tick(Start), Ct));

        await work.AdvanceAsync(interval, Ct);
        work.Delivered.Should().HaveCount(2);

        // The first tick is handed to its handler again after the second has already fired. It parks
        // the key of a due time that is gone, and a fake that forgot finished keys would fire it twice.
        work.Redeliver(work.Delivered[0].MessageId);

        await work.AdvanceAsync(interval * 2, Ct);

        var fired = work.Handled<Tick>().Select(t => t.DueAt).ToArray();
        fired.Should().HaveCount(5);
        fired.Should().Equal(Start, Start + interval, Start, Start + interval * 2, Start + interval * 3);
        work.Delivered.Skip(1).Select(d => d.At).Should().Equal(
            Start + interval, Start + interval, Start + interval * 2, Start + interval * 3);
        work.Waits.Should().ContainSingle().Which.TimeoutAt.Should().Be(Start + interval * 4);
    }

    [Fact]
    public async Task A_handler_that_throws_is_tried_again_after_the_retry_delay_and_then_dead_lettered()
    {
        var work = new InMemoryDurableWork(Start) { MaxAttempts = 3, RetryDelay = TimeSpan.FromMinutes(1) }
            .Handle<Ping>((_, _, _, _) => throw new InvalidOperationException("provider down"));

        await CommitAsync(work, "alpha", unit => unit.EnqueueAsync("alpha", new Ping("doomed"), Ct));

        (await work.RunDueAsync(Ct)).Should().Be(1);
        work.Pending.Should().ContainSingle().Which.DueAt.Should().Be(Start.AddMinutes(1));
        (await work.AdvanceAsync(TimeSpan.FromSeconds(59), Ct)).Should().Be(0, "the retry is not due yet");

        await work.AdvanceAsync(TimeSpan.FromMinutes(10), Ct);

        work.Delivered.Should().HaveCount(3);
        work.Delivered.Select(d => d.Attempt).Should().Equal(1, 2, 3);
        work.Delivered.Select(d => d.At).Should().Equal(Start, Start.AddMinutes(1), Start.AddMinutes(2));
        work.Delivered.Select(d => d.MessageId).Distinct().Should().ContainSingle("every attempt is the same message");
        work.Delivered.Should().OnlyContain(d => d.Error is InvalidOperationException);
        work.DeadLetters.Should().ContainSingle().Which.Reason.Should().Be("provider down");
        work.Pending.Should().BeEmpty();
        work.Handled<Ping>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_handler_that_recovers_is_not_dead_lettered()
    {
        var calls = 0;
        var work = new InMemoryDurableWork(Start) { MaxAttempts = 3 }.Handle<Ping>((_, _, _, _) =>
        {
            calls++;
            return calls == 1 ? throw new InvalidOperationException("provider down") : Task.CompletedTask;
        });

        await CommitAsync(work, "alpha", unit => unit.EnqueueAsync("alpha", new Ping("recovers"), Ct));
        await work.AdvanceAsync(TimeSpan.FromMinutes(10), Ct);

        work.Delivered.Should().HaveCount(2);
        work.Delivered.Select(d => d.At).Should().Equal(Start, Start + work.RetryDelay);
        work.Handled<Ping>().Should().ContainSingle();
        work.DeadLetters.Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_with_no_handler_is_a_failed_attempt_and_ends_as_a_dead_letter()
    {
        var work = new InMemoryDurableWork(Start) { MaxAttempts = 2 };

        await CommitAsync(work, "alpha", unit => unit.EnqueueAsync("alpha", new Ping("nobody listens"), Ct));
        await work.AdvanceAsync(TimeSpan.FromMinutes(10), Ct);

        work.Delivered.Should().HaveCount(2);
        work.DeadLetters.Should().ContainSingle().Which.Reason.Should().Contain(nameof(Ping));
    }

    [Fact]
    public async Task A_handler_receives_what_was_stored_and_not_the_instance_that_was_queued()
    {
        var received = new List<Counter>();
        var work = new InMemoryDurableWork(Start).Handle<Counter>((counter, _, _, _) =>
        {
            received.Add(counter);
            return Task.CompletedTask;
        });

        var queued = new Counter { Value = 1 };

        await CommitAsync(work, "alpha", async unit =>
        {
            await unit.EnqueueAsync("alpha", queued, Ct);
            queued.Value = 2;
        });

        await work.RunDueAsync(Ct);

        var handled = received.Should().ContainSingle().Which;
        handled.Should().NotBeSameAs(queued);
        handled.Value.Should().Be(1, "the message was written when it was staged, and a later change to the instance is not part of it");
    }

    [Fact]
    public async Task A_redelivered_message_reaches_its_handler_again_with_the_same_id()
    {
        var work = Work();
        await CommitAsync(work, "alpha", unit => unit.EnqueueAsync("alpha", new Ping("twice"), Ct));
        await work.RunDueAsync(Ct);

        var first = work.Delivered.Should().ContainSingle().Which;

        work.Redeliver(first.MessageId);
        (await work.RunDueAsync(Ct)).Should().Be(1);

        work.Delivered.Should().HaveCount(2);
        work.Delivered[1].MessageId.Should().Be(first.MessageId);
        work.Delivered[1].Attempt.Should().Be(2);
        work.Handled<Ping>().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_message_that_was_never_handled_cannot_be_redelivered()
    {
        var work = Work();
        await CommitAsync(work, "alpha", unit => unit.ScheduleAsync("alpha", new Ping("later"), Start.AddHours(1), Ct));

        var pending = work.Pending.Should().ContainSingle().Which;

        var unknown = () => work.Redeliver("message-404");
        unknown.Should().Throw<ArgumentException>();

        var notYet = () => work.Redeliver(pending.MessageId);
        notYet.Should().Throw<InvalidOperationException>();

        work.Pending.Should().ContainSingle();
    }

    [Fact]
    public async Task A_handler_that_queues_itself_with_no_delay_is_stopped_and_reported()
    {
        var work = new InMemoryDurableWork(Start).Handle<Ping>(async (ping, context, unit, ct) =>
        {
            await unit.EnqueueAsync(context.Tenant, ping, ct);
            unit.Commit();
        });

        await CommitAsync(work, "alpha", unit => unit.EnqueueAsync("alpha", new Ping("forever"), Ct));

        var run = async () => await work.RunDueAsync(Ct);
        await run.Should().ThrowAsync<InvalidOperationException>();

        work.Delivered.Should().HaveCount(InMemoryDurableWork.MaxDeliveriesPerCall);
        work.Pending.Should().ContainSingle("the bound stops the run and leaves the next message where it was");
    }

    [Fact]
    public void A_message_type_has_one_handler()
    {
        var work = Work();

        var again = () => work.Handle<Ping>((_, _, _, _) => Task.CompletedTask);

        again.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task The_clock_does_not_move_backwards_and_a_closed_unit_of_work_takes_nothing()
    {
        var work = Work();

        var back = async () => await work.AdvanceToAsync(Start.AddTicks(-1), Ct);
        await back.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var negative = async () => await work.AdvanceAsync(TimeSpan.FromTicks(-1), Ct);
        await negative.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var unit = work.Begin("alpha");
        unit.Commit();
        unit.Committed.Should().BeTrue();

        var late = async () => await unit.EnqueueAsync("alpha", new Ping("after the save"), Ct);
        await late.Should().ThrowAsync<InvalidOperationException>();

        var twice = () => unit.Commit();
        twice.Should().Throw<InvalidOperationException>();

        work.Now.Should().Be(Start);
    }
}
