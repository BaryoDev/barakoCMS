namespace barakoCMS.Core.Expressions;

/// <summary>Date arithmetic shared by the formula functions and the computed field functions.</summary>
internal static class DateMath
{
    /// <summary>
    /// Whole months from one day to another, ignoring the time of day. The anniversary is the same day
    /// of the month, or the last day when that month is shorter, so 31 January to 28 February is one
    /// month and 29 February to 28 February a year on is twelve. A later day first gives a negative.
    /// </summary>
    public static int Months(DateTime from, DateTime to)
    {
        var a = from.Date;
        var b = to.Date;
        if (b < a)
            return -Months(b, a);

        var months = ((b.Year - a.Year) * 12) + b.Month - a.Month;
        var anniversary = Math.Min(a.Day, DateTime.DaysInMonth(b.Year, b.Month));
        return b.Day < anniversary ? months - 1 : months;
    }

    /// <summary>Whole years, rounded toward zero: the whole months divided by twelve.</summary>
    public static int Years(DateTime from, DateTime to) => Months(from, to) / 12;

    public static decimal Hours(TimeSpan span) => (decimal)span.TotalHours;

    public static decimal Days(TimeSpan span) => (decimal)span.TotalDays;

    public static decimal Minutes(TimeSpan span) => (decimal)span.TotalMinutes;
}
