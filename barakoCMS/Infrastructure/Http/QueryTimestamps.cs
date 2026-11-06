namespace barakoCMS.Infrastructure.Http;

internal static class QueryTimestamps
{
    /// <summary>
    /// A query-string timestamp with an offset binds as local time, and stored timestamps are UTC, so on
    /// any host not running in UTC the window would be shifted by the zone. One without an offset is
    /// taken as UTC, which is what the From and To filters document.
    /// </summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Utc => value,
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
