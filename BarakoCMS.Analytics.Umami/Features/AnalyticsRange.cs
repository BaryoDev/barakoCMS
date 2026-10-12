namespace BarakoCMS.Analytics.Umami.Features;

/// <summary>Maps the admin's range token ("24h" | "7d" | "30d" | "90d"), or an explicit from and
/// to, to a Umami start/end window (unix milliseconds) and a sensible bucket unit for the trend
/// series.</summary>
internal static class AnalyticsRange
{
    /// <summary>The longest window an explicit from and to may ask for.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(400);

    public static (long StartAt, long EndAt, string Unit) Resolve(string? range)
    {
        var now = DateTimeOffset.UtcNow;
        var (span, unit) = (range?.ToLowerInvariant()) switch
        {
            "24h" => (TimeSpan.FromHours(24), "hour"),
            "30d" => (TimeSpan.FromDays(30), "day"),
            "90d" => (TimeSpan.FromDays(90), "day"),
            _ => (TimeSpan.FromDays(7), "day"), // default 7d
        };
        return (now.Subtract(span).ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), unit);
    }

    /// <summary>The explicit window when the request names one, else the range token's.</summary>
    /// <remarks>
    /// A window of two days or less is bucketed by hour, as <c>24h</c> is, and anything longer by
    /// day. The validators have already refused a from without a to, a from after its to, and a
    /// window longer than <see cref="MaxWindow"/>.
    /// </remarks>
    public static (long StartAt, long EndAt, string Unit) Resolve(string? range, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is not { } start || to is not { } end)
        {
            return Resolve(range);
        }

        var unit = end - start <= TimeSpan.FromHours(48) ? "hour" : "day";
        return (start.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds(), unit);
    }

    /// <summary>The window of the same length that ends where this one starts.</summary>
    public static (long StartAt, long EndAt) Previous(long startAt, long endAt) =>
        (startAt - (endAt - startAt), startAt);

    public static DateTimeOffset At(long unixMilliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
}
