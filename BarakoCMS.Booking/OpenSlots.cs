namespace BarakoCMS.Booking;

/// <summary>A stretch of time, as a booking or a slot holds it. The end is exclusive.</summary>
internal readonly record struct Stretch(DateTimeOffset Start, DateTimeOffset End)
{
    public bool Overlaps(Stretch other) => Start < other.End && End > other.Start;
}

/// <summary>
/// Works out when a resource is free: its opening hours in its own time zone, less the dates it is
/// closed, less the bookings that hold a stretch.
/// </summary>
/// <remarks>
/// Hours are read as local wall clock time and turned into instants date by date, so a window that
/// crosses a clock change keeps its wall clock start and end. A start the clock skips (02:30 on the
/// night it jumps from 02:00 to 03:00) begins when the clock next shows a time, and an hour the
/// clock shows twice is taken at its first showing.
/// </remarks>
internal static class OpenSlots
{
    public static bool TryTimeZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary><c>HH:mm</c> from 00:00 to 23:59, or <c>24:00</c> for the end of the day.</summary>
    public static bool TryClock(string? text, out TimeSpan time)
    {
        time = default;
        if (text is null || text.Length != 5 || text[2] != ':')
        {
            return false;
        }

        if (text == "24:00")
        {
            time = TimeSpan.FromHours(24);
            return true;
        }

        return TimeSpan.TryParseExact(text, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out time);
    }

    public static bool TryDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out date);

    public static DateTimeOffset ToInstant(DateTime wallClock, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (zone.IsInvalidTime(local))
        {
            offset = zone.GetUtcOffset(local.AddHours(-3));
        }
        else if (zone.IsAmbiguousTime(local))
        {
            offset = zone.GetAmbiguousTimeOffsets(local).Max();
        }
        else
        {
            offset = zone.GetUtcOffset(local);
        }

        return new DateTimeOffset(local - offset, TimeSpan.Zero);
    }

    /// <summary>The stretches the resource is open that touch <paramref name="from"/> to <paramref name="to"/>, in order.</summary>
    public static List<Stretch> Windows(BookingResource resource, TimeZoneInfo zone, DateTimeOffset from, DateTimeOffset to)
    {
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime).AddDays(-1);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, zone).DateTime).AddDays(1);
        var result = new List<Stretch>();

        for (var date = first; date <= last; date = date.AddDays(1))
        {
            foreach (var (start, end) in HoursOn(resource, date))
            {
                var midnight = date.ToDateTime(TimeOnly.MinValue);
                var span = new Stretch(ToInstant(midnight + start, zone), ToInstant(midnight + end, zone));
                if (span.End > span.Start && span.Overlaps(new Stretch(from, to)))
                {
                    result.Add(span);
                }
            }
        }

        return result.OrderBy(s => s.Start).ToList();
    }

    private static IEnumerable<(TimeSpan Start, TimeSpan End)> HoursOn(BookingResource resource, DateOnly date)
    {
        var exception = resource.Exceptions.FirstOrDefault(e => TryDate(e.Date, out var d) && d == date);
        if (exception is not null)
        {
            return exception.Closed ? [] : Clocks(exception.Hours.Select(h => (h.Start, h.End)));
        }

        return Clocks(resource.OpeningHours
            .Where(w => Enum.TryParse<DayOfWeek>(w.Day, true, out var day) && day == date.DayOfWeek)
            .Select(w => (w.Start, w.End)));
    }

    private static IEnumerable<(TimeSpan, TimeSpan)> Clocks(IEnumerable<(string Start, string End)> windows)
    {
        foreach (var (start, end) in windows)
        {
            if (TryClock(start, out var s) && TryClock(end, out var e) && e > s)
            {
                yield return (s, e);
            }
        }
    }

    /// <summary>
    /// What is free between <paramref name="from"/> and <paramref name="to"/>, never earlier than
    /// <paramref name="now"/>: fixed slots when the resource has a slot length, else the open
    /// stretches between bookings. At most <paramref name="max"/> are returned, and
    /// <c>Truncated</c> says when there were more.
    /// </summary>
    public static (List<Stretch> Free, bool Truncated) Free(
        BookingResource resource,
        TimeZoneInfo zone,
        IReadOnlyList<Stretch> booked,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now,
        int max)
    {
        var free = new List<Stretch>();
        var range = new Stretch(from > now ? from : now, to);
        if (range.End <= range.Start)
        {
            return (free, false);
        }

        foreach (var window in Windows(resource, zone, from, to))
        {
            if (resource.SlotMinutes is { } minutes and > 0)
            {
                var length = TimeSpan.FromMinutes(minutes);
                for (var start = window.Start; start + length <= window.End; start += length)
                {
                    var slot = new Stretch(start, start + length);
                    if (slot.Start >= range.Start && slot.End <= range.End && !booked.Any(slot.Overlaps) && Add(free, slot, max))
                    {
                        return (free, true);
                    }
                }

                continue;
            }

            var cursor = window.Start > range.Start ? window.Start : range.Start;
            var limit = window.End < range.End ? window.End : range.End;
            var holds = booked.Where(b => b.Overlaps(new Stretch(cursor, limit))).OrderBy(b => b.Start).ToList();
            foreach (var hold in holds)
            {
                if (hold.Start > cursor && Add(free, new Stretch(cursor, hold.Start), max))
                {
                    return (free, true);
                }

                if (hold.End > cursor)
                {
                    cursor = hold.End;
                }
            }

            if (limit > cursor && Add(free, new Stretch(cursor, limit), max))
            {
                return (free, true);
            }
        }

        return (free, false);
    }

    /// <summary>Adds the span, and returns true when that passed the limit and it was dropped.</summary>
    private static bool Add(List<Stretch> free, Stretch span, int max)
    {
        if (free.Count >= max)
        {
            return true;
        }

        free.Add(span);
        return false;
    }
}
