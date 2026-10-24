using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace BarakoCMS.Booking;

/// <summary>Reading the bookings that hold a resource.</summary>
internal static class BookedStretches
{
    private const int BatchSize = 500;

    /// <summary>
    /// A case-insensitive match of one data field. Both the field name and the value are bound
    /// parameters.
    /// </summary>
    private const string FieldEqualsIgnoreCaseSql =
        "lower((SELECT e.value FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower(?) LIMIT 1) #>> '{}') = lower(?)";

    /// <summary>
    /// Keeps a row when the leading date of one data field passes <c>p {0} ?</c>, and also when the
    /// field is missing or does not start with an ISO date, since the C# reader decides those. The
    /// value is bound first and the field name second, the order they appear in the text.
    /// </summary>
    private static string DateFilterSql(string comparison) =>
        "(SELECT CASE WHEN t.p ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' THEN t.p " + comparison + " ? ELSE true END "
        + "FROM (SELECT left(btrim((SELECT e.value FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower(?) LIMIT 1) #>> '{}'), 10) AS p) t)";

    private static readonly string EndsOnOrAfterSql = DateFilterSql(">=");

    private static readonly string StartsOnOrBeforeSql = DateFilterSql("<=");

    public static bool IsCancelled(IReadOnlyDictionary<string, object> data, BookingOptions options) =>
        BookingData.String(data, options.StatusField) is { } status
        && options.CancelledStatuses.Any(c => string.Equals(c?.Trim(), status.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every booking of the resource that still holds its time, apart from <paramref name="except"/>.
    /// A booking whose start or end cannot be read holds nothing.
    /// </summary>
    /// <remarks>
    /// With <paramref name="from"/> and <paramref name="to"/> only bookings that can overlap that
    /// range are read. The query keeps a booking whose end date is within two days of the range start or
    /// later, and whose start date is within two days of the range end or earlier. The margin covers
    /// every UTC offset. A row whose field is missing or does not start with an ISO date is kept,
    /// so the filter only ever keeps too much, never too little, and the caller still compares the
    /// exact instants.
    /// </remarks>
    public static async Task<List<Stretch>> HeldAsync(
        IQuerySession session, BookingOptions options, Guid resource, Guid? except, CancellationToken ct,
        DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var notBefore = from?.UtcDateTime.AddDays(-2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var notAfter = to?.UtcDateTime.AddDays(2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var type = options.ContentType;
        var held = new List<Stretch>();

        for (var skip = 0; ; skip += BatchSize)
        {
            var query = session.Query<Content>()
                .Where(c => c.ContentType == type
                            && c.MatchesSql(FieldEqualsIgnoreCaseSql, options.ResourceField, resource.ToString()));
            if (notBefore is not null)
            {
                query = query.Where(c => c.MatchesSql(EndsOnOrAfterSql, notBefore, options.EndField));
            }

            if (notAfter is not null)
            {
                query = query.Where(c => c.MatchesSql(StartsOnOrBeforeSql, notAfter, options.StartField));
            }

            var batch = await query
                .OrderBy(c => c.Id)
                .Skip(skip)
                .Take(BatchSize)
                .ToListAsync(ct);

            foreach (var entry in batch.Where(c => c.Id != except && !IsCancelled(c.Data, options)))
            {
                if (BookingData.Instant(entry.Data, options.StartField) is { } start
                    && BookingData.Instant(entry.Data, options.EndField) is { } end
                    && end > start)
                {
                    held.Add(new Stretch(start, end));
                }
            }

            if (batch.Count < BatchSize)
            {
                return held;
            }
        }
    }
}
