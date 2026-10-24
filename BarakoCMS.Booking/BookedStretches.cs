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
    /// The leading calendar date of one data field, as text. An instant that can be read starts with
    /// its date, so comparing these as text never needs a cast that a bad value could break.
    /// </summary>
    private const string FieldDateSql =
        "left((SELECT e.value FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower(?) LIMIT 1) #>> '{}', 10)";

    private const string EndsOnOrAfterSql = FieldDateSql + " >= ?";

    private const string StartsOnOrBeforeSql = FieldDateSql + " <= ?";

    public static bool IsCancelled(IReadOnlyDictionary<string, object> data, BookingOptions options) =>
        BookingData.String(data, options.StatusField) is { } status
        && options.CancelledStatuses.Any(c => string.Equals(c?.Trim(), status.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every booking of the resource that still holds its time, apart from <paramref name="except"/>.
    /// A booking whose start or end cannot be read holds nothing.
    /// </summary>
    /// <remarks>
    /// With <paramref name="from"/> and <paramref name="to"/> only bookings that can overlap that
    /// range are read. The query keeps a booking whose end date is within a day of the range start or
    /// later, and whose start date is within a day of the range end or earlier. A day either side
    /// covers every UTC offset, so the filter only ever keeps too much, never too little, and the
    /// caller still compares the exact instants.
    /// </remarks>
    public static async Task<List<Stretch>> HeldAsync(
        IQuerySession session, BookingOptions options, Guid resource, Guid? except, CancellationToken ct,
        DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var notBefore = from?.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var notAfter = to?.UtcDateTime.AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var type = options.ContentType;
        var held = new List<Stretch>();

        for (var skip = 0; ; skip += BatchSize)
        {
            var query = session.Query<Content>()
                .Where(c => c.ContentType == type
                            && c.MatchesSql(FieldEqualsIgnoreCaseSql, options.ResourceField, resource.ToString()));
            if (notBefore is not null)
            {
                query = query.Where(c => c.MatchesSql(EndsOnOrAfterSql, options.EndField, notBefore));
            }

            if (notAfter is not null)
            {
                query = query.Where(c => c.MatchesSql(StartsOnOrBeforeSql, options.StartField, notAfter));
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
