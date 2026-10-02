using barakoCMS.Infrastructure.Jobs;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class JobBackoffTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    public void Each_failure_doubles_the_wait(int attempt, int expectedSeconds)
    {
        JobBackoff.DelayFor(attempt, baseSeconds: 30, maxSeconds: 3600)
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void The_wait_is_capped_at_the_max()
    {
        // 30 * 2^7 is 3840, past the cap.
        JobBackoff.DelayFor(8, baseSeconds: 30, maxSeconds: 3600).Should().Be(TimeSpan.FromSeconds(3600));
    }

    [Fact]
    public void A_very_high_attempt_count_stays_at_the_cap_rather_than_overflowing()
    {
        JobBackoff.DelayFor(200, baseSeconds: 30, maxSeconds: 3600).Should().Be(TimeSpan.FromSeconds(3600));
    }

    [Fact]
    public void A_zero_base_means_no_wait()
    {
        JobBackoff.DelayFor(3, baseSeconds: 0, maxSeconds: 3600).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void An_attempt_below_one_is_treated_as_the_first()
    {
        JobBackoff.DelayFor(0, baseSeconds: 30, maxSeconds: 3600).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Fifty_jittered_waits_for_the_same_attempt_are_spread_out()
    {
        var random = new Random(7);

        var waits = Enumerable.Range(0, 50)
            .Select(_ => JobBackoff.DelayFor(3, baseSeconds: 30, maxSeconds: 3600, random))
            .ToList();

        waits.Should().HaveCount(50);
        waits.Distinct().Count().Should().BeGreaterThan(40, "jobs that failed together must not retry together");
        (waits.Max() - waits.Min()).Should().BeGreaterThan(TimeSpan.FromSeconds(10),
            "the unjittered wait is 120 seconds, and a quarter of that is the room the jitter has");
    }

    [Fact]
    public void A_thousand_jittered_waits_stay_within_the_cap()
    {
        var random = new Random(11);

        var waits = Enumerable.Range(0, 1000)
            .Select(i => JobBackoff.DelayFor(1 + i % 40, baseSeconds: 30, maxSeconds: 3600, random))
            .ToList();

        waits.Should().HaveCount(1000);
        waits.Should().OnlyContain(wait => wait <= TimeSpan.FromSeconds(3600), "jitter never passes the cap");
        waits.Count(wait => wait > TimeSpan.FromSeconds(2700)).Should().BeGreaterThan(500,
            "most of these attempts are at the cap, and jitter takes off a quarter at most");
    }

    [Fact]
    public void Jitter_takes_off_a_quarter_of_the_wait_at_most()
    {
        var random = new Random(3);

        var waits = Enumerable.Range(0, 200)
            .Select(_ => JobBackoff.DelayFor(2, baseSeconds: 30, maxSeconds: 3600, random))
            .ToList();

        waits.Should().HaveCount(200);
        waits.Should().OnlyContain(wait => wait >= TimeSpan.FromSeconds(45) && wait <= TimeSpan.FromSeconds(60));
    }
}
