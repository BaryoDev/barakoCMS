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
}
