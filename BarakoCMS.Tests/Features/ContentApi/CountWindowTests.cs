using barakoCMS.Features.Content.Counts;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>How a count window reads its parameters. No host needed.</summary>
public class CountWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_time_with_no_zone_is_read_as_utc_whatever_the_server_zone_is()
    {
        var (window, refusal) = CountWindow.Resolve(null, "2026-03-10T00:00:00", "2026-03-13T00:00:00", null, Now);

        refusal.Should().BeNull();
        window!.From.Should().Be(new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero));
        window.From.Offset.Should().Be(TimeSpan.Zero);
        window.To.Should().Be(new DateTimeOffset(2026, 3, 13, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_time_with_an_offset_keeps_it_and_means_the_same_instant()
    {
        var (window, refusal) = CountWindow.Resolve(null, "2026-03-10T08:00:00+08:00", "2026-03-13T08:00:00+08:00", null, Now);

        refusal.Should().BeNull();
        window!.From.UtcDateTime.Should().Be(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_from_before_the_floor_is_refused_instead_of_running_the_previous_window_off_the_calendar()
    {
        var (window, refusal) = CountWindow.Resolve(null, "0001-01-01T00:00:00Z", "0001-01-02T00:00:00Z", null, Now);

        window.Should().BeNull();
        refusal.Should().Contain("on or after 2000-01-01");
    }

    [Fact]
    public void A_from_that_is_not_a_time_is_refused()
    {
        CountWindow.Resolve(null, "yesterday", null, null, Now).Refusal.Should().Contain("From must be a date and time");
        CountWindow.Resolve(null, "2026-03-10T00:00:00Z", "soon", null, Now).Refusal.Should().Contain("To must be a date and time");
    }

    [Fact]
    public void A_from_exactly_ninety_days_back_survives_the_clock_moving_on_between_two_reads()
    {
        var from = (Now - TimeSpan.FromDays(90)).ToString("o");
        var later = Now.AddSeconds(30);

        CountWindow.Resolve(null, from, null, null, Now).Refusal.Should().BeNull("the validator's read");
        CountWindow.Resolve(null, from, null, null, later).Refusal.Should().NotBeNull("with no slack the second read is past the cap");
        CountWindow.Resolve(null, from, null, null, later, CountWindow.ClockSlack).Refusal.Should().BeNull("the handler's read, with the slack");
    }

    [Fact]
    public void A_series_never_has_more_buckets_than_the_cap_even_when_the_slack_is_used()
    {
        var from = (Now - TimeSpan.FromHours(200)).ToString("o");

        var (window, refusal) = CountWindow.Resolve(null, from, null, "hour", Now.AddSeconds(30), CountWindow.ClockSlack);

        refusal.Should().BeNull();
        window!.Buckets.Should().Be(CountWindow.MaxBuckets);
    }
}
