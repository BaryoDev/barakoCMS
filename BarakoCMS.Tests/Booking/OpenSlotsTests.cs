using BarakoCMS.Booking;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Booking;

/// <summary>The availability arithmetic, with no database.</summary>
public class OpenSlotsTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static BookingResource Resource(string zone, int? slot, params (string Day, string Start, string End)[] hours) => new()
    {
        Id = Guid.NewGuid(),
        Name = "r",
        TimeZone = zone,
        SlotMinutes = slot,
        OpeningHours = hours.Select(h => new OpeningWindow { Day = h.Day, Start = h.Start, End = h.End }).ToList(),
    };

    private static (List<Stretch> Free, bool Truncated) Free(
        BookingResource r, DateTimeOffset from, DateTimeOffset to, params Stretch[] booked)
    {
        OpenSlots.TryTimeZone(r.TimeZone, out var zone).Should().BeTrue();
        return OpenSlots.Free(r, zone, booked, from, to, Now, 1000);
    }

    [Fact]
    public void Slots_follow_the_opening_hours_in_the_resource_time_zone()
    {
        // 2031-03-03 is a Monday. Manila is UTC+8, so 09:00 local is 01:00Z.
        var r = Resource("Asia/Manila", 60, ("Monday", "09:00", "12:00"));
        var (free, _) = Free(r, new(2031, 3, 3, 0, 0, 0, TimeSpan.FromHours(8)), new(2031, 3, 4, 0, 0, 0, TimeSpan.FromHours(8)));

        free.Should().HaveCount(3);
        free[0].Start.Should().Be(new DateTimeOffset(2031, 3, 3, 1, 0, 0, TimeSpan.Zero));
        free[2].End.Should().Be(new DateTimeOffset(2031, 3, 3, 4, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_booking_removes_the_slots_it_overlaps_and_an_adjacent_one_removes_none()
    {
        var r = Resource("UTC", 60, ("Monday", "09:00", "12:00"));
        var from = new DateTimeOffset(2031, 3, 3, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(1);

        var (overlapping, _) = Free(r, from, to, new Stretch(from.AddHours(9.5), from.AddHours(10.5)));
        overlapping.Should().HaveCount(1, "the 09:00 and 10:00 slots are both touched, leaving 11:00");

        var (adjacent, _) = Free(r, from, to, new Stretch(from.AddHours(8), from.AddHours(9)));
        adjacent.Should().HaveCount(3, "a booking that ends at 09:00 does not touch the 09:00 slot");
    }

    [Fact]
    public void A_closed_exception_removes_the_day_and_hours_replace_the_weekly_ones()
    {
        var r = Resource("UTC", 60, ("Monday", "09:00", "12:00"));
        var from = new DateTimeOffset(2031, 3, 3, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(1);
        Free(r, from, to).Free.Should().HaveCount(3);

        r.Exceptions.Add(new ResourceException { Date = "2031-03-03", Closed = true });
        Free(r, from, to).Free.Should().BeEmpty();

        r.Exceptions[0] = new ResourceException
        {
            Date = "2031-03-03",
            Closed = false,
            Hours = [new DayWindow { Start = "13:00", End = "15:00" }],
        };
        var (replaced, _) = Free(r, from, to);
        replaced.Should().HaveCount(2);
        replaced[0].Start.Should().Be(from.AddHours(13));
    }

    [Fact]
    public void A_day_the_clock_springs_forward_is_an_hour_short()
    {
        // 2031-03-09 is a Sunday, the second of March, when New York skips 02:00 to 03:00.
        var r = Resource("America/New_York", null, ("Sunday", "00:00", "24:00"));
        var from = new DateTimeOffset(2031, 3, 9, 0, 0, 0, TimeSpan.FromHours(-5));
        var (free, _) = Free(r, from, from.AddDays(1));

        free.Should().ContainSingle();
        (free[0].End - free[0].Start).Should().Be(TimeSpan.FromHours(23));
    }

    [Fact]
    public void A_day_the_clock_falls_back_is_an_hour_long()
    {
        var r = Resource("America/New_York", null, ("Sunday", "00:00", "24:00"));
        var from = new DateTimeOffset(2031, 11, 2, 0, 0, 0, TimeSpan.FromHours(-4));
        var (free, _) = Free(r, from, from.AddDays(1).AddHours(1));

        free.Should().ContainSingle();
        (free[0].End - free[0].Start).Should().Be(TimeSpan.FromHours(25));
    }

    [Fact]
    public void Wall_clock_hours_keep_their_local_time_across_a_clock_change()
    {
        var r = Resource("America/New_York", 60, ("Saturday", "09:00", "10:00"), ("Sunday", "09:00", "10:00"));
        var before = new DateTimeOffset(2031, 3, 8, 0, 0, 0, TimeSpan.FromHours(-5));
        var (free, _) = Free(r, before, before.AddDays(2));

        free.Should().HaveCount(2);
        free[0].Start.Should().Be(new DateTimeOffset(2031, 3, 8, 14, 0, 0, TimeSpan.Zero), "09:00 EST");
        free[1].Start.Should().Be(new DateTimeOffset(2031, 3, 9, 13, 0, 0, TimeSpan.Zero), "09:00 EDT");
    }

    [Fact]
    public void A_free_form_resource_answers_the_stretches_between_bookings()
    {
        var r = Resource("UTC", null, ("Monday", "09:00", "17:00"));
        var day = new DateTimeOffset(2031, 3, 3, 0, 0, 0, TimeSpan.Zero);
        var (free, _) = Free(r, day, day.AddDays(1), new Stretch(day.AddHours(11), day.AddHours(12)));

        free.Should().HaveCount(2);
        free[0].Should().Be(new Stretch(day.AddHours(9), day.AddHours(11)));
        free[1].Should().Be(new Stretch(day.AddHours(12), day.AddHours(17)));
    }

    [Fact]
    public void A_slot_that_has_already_started_is_not_offered()
    {
        var r = Resource("UTC", 60, ("Monday", "09:00", "12:00"));
        var day = new DateTimeOffset(2031, 3, 3, 0, 0, 0, TimeSpan.Zero);
        OpenSlots.TryTimeZone("UTC", out var zone);

        var (free, _) = OpenSlots.Free(r, zone, [], day, day.AddDays(1), day.AddHours(10).AddMinutes(1), 1000);

        free.Should().ContainSingle();
        free[0].Start.Should().Be(day.AddHours(11));
    }

    [Fact]
    public void The_slot_limit_truncates_and_says_so()
    {
        var r = Resource("UTC", 15, ("Monday", "09:00", "12:00"));
        var day = new DateTimeOffset(2031, 3, 3, 0, 0, 0, TimeSpan.Zero);
        OpenSlots.TryTimeZone("UTC", out var zone);

        var (free, truncated) = OpenSlots.Free(r, zone, [], day, day.AddDays(1), Now, 4);

        free.Should().HaveCount(4);
        truncated.Should().BeTrue();
    }

    [Theory]
    [InlineData("Asia/Manila", true)]
    [InlineData("Not/AZone", false)]
    [InlineData("", false)]
    public void Only_a_real_time_zone_is_accepted(string id, bool valid) =>
        OpenSlots.TryTimeZone(id, out _).Should().Be(valid);
}
