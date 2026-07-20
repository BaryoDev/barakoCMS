namespace BarakoCMS.Analytics.Umami.Features;

/// <summary>Maps the admin's range token ("24h" | "7d" | "30d" | "90d") to a Umami start/end window
/// (unix milliseconds) and a sensible bucket unit for the trend series.</summary>
internal static class AnalyticsRange
{
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
}
