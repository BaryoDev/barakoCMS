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

    private static Task ParkAsync(InMemoryDurableWork work, string tenant, string waitKey) =>
        CommitAsync(work, tenant, async unit =>
            (await unit.WaitAsync(waitKey, Start.AddHours(1), new TimedOut(waitKey), Ct)).Should().BeTrue());

    [Fact]
    public async Task A_message_is_handled_only_after_its_unit_of_work_commits()
    {
        var work = Work();

        using (var unit = work.Begin("alpha"))
        {
            await unit.EnqueueAsync(new Ping("committed"), Ct);

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
            await abandoned.EnqueueAsync(new Ping("abandoned"), Ct);
            await abandoned.ScheduleAsync(new Ping("abandoned later"), Start.AddMinutes(5), Ct);
            (await abandoned.StartAsync("tests:run-abandoned", new Ping("abandoned run"), Ct)).Should().BeTrue();
            (await abandoned.WaitAsync("tests:wait-abandoned", Start.AddMinutes(5), new TimedOut("abandoned"), Ct)).Should().BeTrue();
        }

        // The control. Without it, a fake that delivered nothing at all would pass.
        await CommitAsync(work, "alpha", unit => unit.EnqueueAsync(new Ping("kept"), Ct));

        await work.AdvanceAsync(TimeSpan.FromHours(1), Ct);

        work.Delivered.Should().HaveCount(1);
        work.Handled<Ping>().Should().ContainSingle().Which.Text.Should().Be("kept");
        work.Waits.Should().BeEmpty();
        work.Pending.Should().BeEmpty();

        // An id and a key that were staged and never committed were never used.
        await CommitAsync(work, "alpha", async unit =>
            (await unit.StartAsync("tests:run-abandoned", new Ping("started after all"), Ct)).Should().BeTrue());
        await work.RunDueAsync(Ct);
        work.Handled<Ping>().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_unit_of_work_whose_commit_fails_leaves_nothing_behind()
    {
        var work = Work();
        await ParkAsync(work, "alpha", "tests:reply-1");

        using var first = work.Begin("alpha");
        using var second = work.Begin("alpha");

        (await first.ResumeAsync("tests:reply-1", new Reply("first"), Ct)).Should().BeTrue();
        (await second.ResumeAsync("tests:reply-1", new Reply("second"), Ct)).Should().BeTrue(
            "neither resume has committed, so both still see the wait");
        await second.EnqueueAsync(new Ping("staged beside the losing resume"), Ct);

        first.Commit();

        var commit = () => second.Commit();
        commit.Should().Throw<DurableWorkConflictException>();

        // What the failed commit held is gone, and is not committed by a later save either.
        second.Commit();

        await work.AdvanceAsync(TimeSpan.FromHours(2), Ct);

        work.Delivered.Should().HaveCount(1);
        work.Handled<Reply>().Should().ContainSingle().Which.Answer.Should().Be("first");
        work.Handled<Ping>().Should().BeEmpty("a message staged in a unit of work that failed to commit must not outlive it");
        work.Handled<TimedOut>().Should().BeEmpty("the wait was resumed, so it does not also time out");
    }

    [Fact]
    public async Task A_message_is_queued_in_the_tenant_of_its_unit_of_work_and_the_handler_is_told_which()
    {
        var contexts = new List<DurableMessageContext>();
        var units = new List<string>();
        var work = new InMemoryDurableWork(Start).Handle<Ping>((_, context, unit, _) =>
        {
            contexts.Add(context);
            units.Add(unit.Tenant);
            return Task.CompletedTask;
        });

        await CommitAsync(work, "alpha", unit => unit.EnqueueAsync(new Ping("for alpha"), Ct));
        await CommitAsync(work, "beta", unit => unit.ScheduleAsync(new Ping("for beta"), Start, Ct));

        await work.RunDueAsync(Ct);

        contexts.Should().HaveCount(2);
        contexts.Select(c => c.Tenant).Should().Equal("alpha", "beta");
        contexts.Select(c => c.MessageId).Should().OnlyHaveUniqueItems();
        units.Should().Equal("alpha", "beta");
        work.Delivered.Select(d => d.Tenant).Should().Equal("alpha", "beta");
    }

    [Fact]
    public async Task The_caller_is_given_the_id_the_handler_sees()
    {
        var seen = new List<string>();
        var work = new InMemoryDurableWork(Start).Handle<Ping>((_, context, _, _) =>
        {
            seen.Add(context.MessageId);
            return Task.CompletedTask;
        });

        string enqueued;
        string scheduled;

        using (var unit = work.Begin("alpha"))
        {
            enqueued = await unit.EnqueueAsync(new Ping("now"), Ct);
            scheduled = await unit.ScheduleAsync(new Ping("later"), Start.AddMinutes(5), Ct);
            unit.Commit();
        }

        enqueued.Should().NotBeNullOrWhiteSpace();
        scheduled.Should().NotBe(enqueued);

        await work.AdvanceAsync(TimeSpan.FromMinutes(5), Ct);

        seen.Should().HaveCount(2);
        seen.Should().Equal(enqueued, scheduled);
    }

    [Fact]
    public async Task What_a_handler_stages_is_kept_only_if_the_handler_commits()
    {
        var work = new InMemoryDurableWork(Start)
            .Handle<Reply>((_, _, _, _) => Task.CompletedTask)
            .Handle<Ping>(async (ping, _, unit, ct) =>
            {
                await unit.EnqueueAsync(new Reply(ping.Text), ct);

                if (ping.Text == "saves")
                {
                    unit.Commit();
                }
            });

        await CommitAsync(work, "alpha", async unit =>
        {
            await unit.EnqueueAsync(new Ping("saves"), Ct);
            await unit.EnqueueAsync(new Ping("forgets to save"), Ct);
        });

        await work.RunDueAsync(Ct);

        work.Handled<Ping>().Should().HaveCount(2);
        work.Handled<Reply>().Should().ContainSingle().Which.Answer.Should().Be("saves");
    }

    [Fact]
    public async Task A_unit_of_work_can_be_committed_more_than_once_and_each_commit_takes_what_was_staged_since()
    {
        var work = Work();

        using (var unit = work.Begin("alpha"))
        {
            await unit.EnqueueAsync(new Ping("first save"), Ct);
            unit.Commit();
            await unit.EnqueueAsync(new Ping("second save"), Ct);
            unit.Commit();
            await unit.EnqueueAsync(new Ping("never saved"), Ct);
        }

        await work.RunDueAsync(Ct);

        work.Handled<Ping>().Should().HaveCount(2);
        work.Handled<Ping>().Select(p => p.Text).Should().Equal("first save", "second save");
    }

    [Fact]
    public async Task A_scheduled_message_is_not_handled_before_its_time()
    {
        var work = Work();
        var dueAt = Start.AddMinutes(10);
        await CommitAsync(work, "alpha", unit => unit.ScheduleAsync(new Ping("later"), dueAt, Ct));

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
            await unit.ScheduleAsync(new Ping("second"), Start.AddMinutes(20), Ct);
            await unit.ScheduleAsync(new Ping("first"), Start.AddMinutes(10), Ct);
            await unit.EnqueueAsync(new Ping("now"), Ct);
            await unit.ScheduleAsync(new Ping("already past"), Start.AddMinutes(-5), Ct);
        });

        (await work.AdvanceAsync(TimeSpan.FromHours(1), Ct)).Should().Be(4);

        work.Handled<Ping>().Select(p => p.Text).Should().Equal("already past", "now", "first", "second");
        work.Delivered.Select(d => d.At).Should().Equal(Start, Start, Start.AddMinutes(10), Start.AddMinutes(20));
        work.Now.Should().Be(Start.AddHours(1));
    }

    [Fact]
    public async Task A_run_id_starts_once_per_tenant_and_a_second_start_is_answered_false()
    {
        var work = Work();

        using (var unit = work.Begin("alpha"))
        {
            (await unit.StartAsync("tests:run-1", new Ping("first"), Ct)).Should().BeTrue();
            (await unit.StartAsync("tests:run-1", new Ping("twice in one unit"), Ct)).Should().BeFalse();
            unit.Commit();
        }

        using (var unit = work.Begin("alpha"))
        {
            (await unit.StartAsync("tests:run-1", new Ping("before it ran"), Ct)).Should().BeFalse();
            unit.Commit();
        }

        await work.RunDueAsync(Ct);

        using (var unit = work.Begin("alpha"))
        {
            (await unit.StartAsync("tests:run-1", new Ping("after it ran"), Ct)).Should().BeFalse();
            (await unit.StartAsync("tests:run-2", new Ping("another run"), Ct)).Should().BeTrue();
            unit.Commit();
        }

        using (var unit = work.Begin("beta"))
        {
            (await unit.StartAsync("tests:run-1", new Ping("another tenant"), Ct)).Should().BeTrue();
            unit.Commit();
        }

        await work.RunDueAsync(Ct);

        work.Handled<Ping>().Should().HaveCount(3);
        work.Handled<Ping>().Select(p => p.Text).Should().Equal("first", "another run", "another tenant");
    }

    [Fact]
    public async Task A_start_that_loses_a_race_fails_to_commit_and_takes_its_unit_of_work_with_it()
    {
        var work = Work();

        using var winner = work.Begin("alpha");
        using var loser = work.Begin("alpha");

        (await winner.StartAsync("tests:run-1", new Ping("winner"), Ct)).Should().BeTrue();
        (await loser.StartAsync("tests:run-1", new Ping("loser"), Ct)).Should().BeTrue(
            "neither start has committed, so the id is still free");

        // Stands for the document a caller stores beside the start.
        await loser.EnqueueAsync(new Ping("the loser's own write"), Ct);

        winner.Commit();

        var commit = () => loser.Commit();
        commit.Should().Throw<DurableWorkConflictException>();

        (await loser.StartAsync("tests:run-1", new Ping("asked again"), Ct)).Should().BeFalse(
            "the caller that asks again is told the id is taken");

        await work.RunDueAsync(Ct);

        work.Handled<Ping>().Should().ContainSingle().Which.Text.Should().Be("winner");
    }

    [Fact]
    public async Task A_resumed_wait_delivers_the_resume_once_and_never_its_timeout()
    {
        var work = Work();
        await ParkAsync(work, "alpha", "tests:reply-1");

        work.Waits.Should().ContainSingle().Which.TimeoutAt.Should().Be(Start.AddHours(1));
        (await work.AdvanceAsync(TimeSpan.FromMinutes(30), Ct)).Should().Be(0, "a parked run does nothing while it waits");

        using (var unit = work.Begin("alpha"))
        {
            (await unit.ResumeAsync("tests:reply-1", new Reply("answered"), Ct)).Should().BeTrue();
            (await unit.ResumeAsync("tests:reply-1", new Reply("answered twice"), Ct)).Should().BeFalse();
            unit.Commit();
        }

        using (var late = work.Begin("alpha"))
        {
            (await late.ResumeAsync("tests:reply-1", new Reply("answered again"), Ct)).Should().BeFalse();
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
        await ParkAsync(work, "alpha", "tests:reply-1");

        (await work.AdvanceToAsync(Start.AddHours(1) - TimeSpan.FromTicks(1), Ct)).Should().Be(0);
        (await work.AdvanceAsync(TimeSpan.FromTicks(1), Ct)).Should().Be(1);

        using (var late = work.Begin("alpha"))
        {
            (await late.ResumeAsync("tests:reply-1", new Reply("too late"), Ct)).Should().BeFalse();
            late.Commit();
        }

        await work.RunDueAsync(Ct);

        work.Delivered.Should().HaveCount(1);
        work.Handled<TimedOut>().Should().ContainSingle().Which.WaitKey.Should().Be("tests:reply-1");
        work.Handled<Reply>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_resume_staged_before_the_timeout_and_committed_after_it_fails_to_commit()
    {
        var work = Work();
        await ParkAsync(work, "alpha", "tests:reply-1");

        using var slow = work.Begin("alpha");
        (await slow.ResumeAsync("tests:reply-1", new Reply("slow"), Ct)).Should().BeTrue();

        await work.AdvanceAsync(TimeSpan.FromHours(1), Ct);

        var commit = () => slow.Commit();
        commit.Should().Throw<DurableWorkConflictException>();

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
            (await unit.WaitAsync("tests:reply-1", Start.AddHours(1), new TimedOut("tests:reply-1"), Ct)).Should().BeTrue();

            (await unit.ResumeAsync("tests:reply-1", new Reply("too early"), Ct)).Should().BeFalse(
                "the wait is staged and has not committed");

            unit.Commit();
        }

        using (var other = work.Begin("beta"))
        {
            (await other.ResumeAsync("tests:reply-1", new Reply("wrong tenant"), Ct)).Should().BeFalse();
            other.Commit();
        }

        await work.RunDueAsync(Ct);
        work.Delivered.Should().BeEmpty();
        work.Waits.Should().ContainSingle().Which.Tenant.Should().Be("alpha");

        using (var own = work.Begin("alpha"))
        {
            (await own.ResumeAsync("tests:reply-1", new Reply("right tenant"), Ct)).Should().BeTrue();
            own.Commit();
        }

        await work.RunDueAsync(Ct);
        work.Handled<Reply>().Should().ContainSingle().Which.Answer.Should().Be("right tenant");
    }

    [Fact]
    public async Task A_wait_key_that_was_already_used_is_answered_false_and_parks_nothing()
    {
        var work = Work();
        await ParkAsync(work, "alpha", "tests:reply-1");

        using (var unit = work.Begin("alpha"))
        {
            (await unit.WaitAsync("tests:reply-1", Start.AddHours(1), new TimedOut("while it waits"), Ct)).Should().BeFalse();
            (await unit.ResumeAsync("tests:reply-1", new Reply("answered"), Ct)).Should().BeTrue();
            unit.Commit();
        }

        await work.RunDueAsync(Ct);

        using (var unit = work.Begin("alpha"))
        {
            // The handler that parked the run is run again, as after a crash, and parks it again.
            (await unit.WaitAsync("tests:reply-1", Start.AddHours(1), new TimedOut("after the resume"), Ct)).Should().BeFalse();

            // The control: a key that was never used does park, once.
            (await unit.WaitAsync("tests:reply-2", Start.AddHours(1), new TimedOut("tests:reply-2"), Ct)).Should().BeTrue();
            (await unit.WaitAsync("tests:reply-2", Start.AddHours(1), new TimedOut("twice in one unit"), Ct)).Should().BeFalse();
            unit.Commit();
        }

        work.Waits.Should().ContainSingle().Which.WaitKey.Should().Be("tests:reply-2");

        await work.AdvanceAsync(TimeSpan.FromHours(2), Ct);

        work.Handled<TimedOut>().Should().ContainSingle().Which.WaitKey.Should().Be("tests:reply-2");
        work.Handled<Reply>().Should().ContainSingle().Which.Answer.Should().Be("answered");
    }

    [Fact]
    public async Task A_wait_that_loses_a_race_fails_to_commit()
    {
        var work = Work();

        using var winner = work.Begin("alpha");
        using var loser = work.Begin("alpha");

        (await winner.WaitAsync("tests:reply-1", Start.AddHours(1), new TimedOut("winner"), Ct)).Should().BeTrue();
        (await loser.WaitAsync("tests:reply-1", Start.AddHours(2), new TimedOut("loser"), Ct)).Should().BeTrue();

        winner.Commit();

        var commit = () => loser.Commit();
        commit.Should().Throw<DurableWorkConflictException>();

        await work.AdvanceAsync(TimeSpan.FromHours(3), Ct);

        work.Handled<TimedOut>().Should().ContainSingle().Which.WaitKey.Should().Be("winner");
        work.Delivered[0].At.Should().Be(Start.AddHours(1));
    }

    [Fact]
    public async Task A_run_that_parks_itself_until_its_next_due_time_fires_once_per_due_time()
    {
        var interval = TimeSpan.FromHours(1);
        var work = new InMemoryDurableWork(Start).Handle<Tick>(async (tick, _, unit, ct) =>
        {
            var next = tick.DueAt + interval;
            await unit.WaitAsync($"tests:sweep:{next:O}", next, new Tick(next), ct);
            unit.Commit();
        });

        await CommitAsync(work, "alpha", unit => unit.StartAsync($"tests:sweep:{Start:O}", new Tick(Start), Ct));

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
    public async Task A_recurring_run_that_parks_its_next_due_time_before_working_outlives_a_tick_that_is_dead_lettered()
    {
        var interval = TimeSpan.FromHours(1);
        var work = new InMemoryDurableWork(Start) { MaxAttempts = 2, RetryDelay = TimeSpan.FromMinutes(1) }
            .Handle<Tick>(async (tick, _, unit, ct) =>
            {
                var next = tick.DueAt + interval;
                await unit.WaitAsync($"tests:sweep:{next:O}", next, new Tick(next), ct);
                unit.Commit();

                if (tick.DueAt == Start)
                {
                    throw new InvalidOperationException("the work of the first tick always fails");
                }
            });

        await CommitAsync(work, "alpha", unit => unit.StartAsync($"tests:sweep:{Start:O}", new Tick(Start), Ct));

        await work.AdvanceAsync(interval * 2, Ct);

        work.DeadLetters.Should().ContainSingle().Which.Message.Should().Be(new Tick(Start));
        work.Delivered.Count(d => d.Error is not null).Should().Be(2, "the first tick was tried twice and given up on");

        var fired = work.Handled<Tick>().Select(t => t.DueAt).ToArray();
        fired.Should().HaveCount(2);
        fired.Should().Equal(Start + interval, Start + interval * 2);
        work.Waits.Should().ContainSingle().Which.TimeoutAt.Should().Be(Start + interval * 3);
    }

    [Fact]
    public async Task A_handler_that_throws_is_tried_again_after_the_retry_delay_and_then_dead_lettered()
    {
        var work = new InMemoryDurableWork(Start) { MaxAttempts = 3, RetryDelay = TimeSpan.FromMinutes(1) }
            .Handle<Ping>((_, _, _, _) => throw new InvalidOperationException("the provider said: key sk-123 is wrong"));

        await CommitAsync(work, "alpha", unit => unit.EnqueueAsync(new Ping("doomed"), Ct));

        (await work.RunDueAsync(Ct)).Should().Be(1);
        work.Pending.Should().ContainSingle().Which.DueAt.Should().Be(Start.AddMinutes(1));
        (await work.AdvanceAsync(TimeSpan.FromSeconds(59), Ct)).Should().Be(0, "the retry is not due yet");

        await work.AdvanceAsync(TimeSpan.FromMinutes(10), Ct);

        work.Delivered.Should().HaveCount(3);
        work.Delivered.Select(d => d.Attempt).Should().Equal(1, 2, 3);
        work.Delivered.Select(d => d.At).Should().Equal(Start, Start.AddMinutes(1), Start.AddMinutes(2));
        work.Delivered.Select(d => d.MessageId).Distinct().Should().ContainSingle("every attempt is the same message");
        work.Delivered.Should().OnlyContain(d => d.Error is InvalidOperationException);
        work.DeadLetters.Should().ContainSingle().Which.Reason.Should().Be(
            nameof(InvalidOperationException), "the type's name is kept and the message, which can hold a credential, is not");
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

        await CommitAsync(work, "alpha", unit => unit.EnqueueAsync(new Ping("recovers"), Ct));
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

        await CommitAsync(work, "alpha", unit => unit.EnqueueAsync(new Ping("nobody listens"), Ct));
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
            await unit.EnqueueAsync(queued, Ct);
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
        await CommitAsync(work, "alpha", unit => unit.EnqueueAsync(new Ping("twice"), Ct));
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
        await CommitAsync(work, "alpha", unit => unit.ScheduleAsync(new Ping("later"), Start.AddHours(1), Ct));

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
        var work = new InMemoryDurableWork(Start).Handle<Ping>(async (ping, _, unit, ct) =>
        {
            await unit.EnqueueAsync(ping, ct);
            unit.Commit();
        });

        await CommitAsync(work, "alpha", unit => unit.EnqueueAsync(new Ping("forever"), Ct));

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
    public async Task The_clock_does_not_move_backwards_and_a_disposed_unit_of_work_takes_nothing()
    {
        var work = Work();

        var back = async () => await work.AdvanceToAsync(Start.AddTicks(-1), Ct);
        await back.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var negative = async () => await work.AdvanceAsync(TimeSpan.FromTicks(-1), Ct);
        await negative.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var unit = work.Begin("alpha");
        unit.Dispose();

        var late = async () => await unit.EnqueueAsync(new Ping("after the scope ended"), Ct);
        await late.Should().ThrowAsync<ObjectDisposedException>();

        var save = () => unit.Commit();
        save.Should().Throw<ObjectDisposedException>();

        work.Now.Should().Be(Start);
    }
}
