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

    public static bool IsCancelled(IReadOnlyDictionary<string, object> data, BookingOptions options) =>
        BookingData.String(data, options.StatusField) is { } status
        && options.CancelledStatuses.Any(c => string.Equals(c?.Trim(), status.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every booking of the resource that still holds its time, apart from <paramref name="except"/>.
    /// A booking whose start or end cannot be read holds nothing.
    /// </summary>
    public static async Task<List<Stretch>> HeldAsync(
        IQuerySession session, BookingOptions options, Guid resource, Guid? except, CancellationToken ct)
    {
        var type = options.ContentType;
        var held = new List<Stretch>();

        for (var skip = 0; ; skip += BatchSize)
        {
            var batch = await session.Query<Content>()
                .Where(c => c.ContentType == type
                            && c.MatchesSql(FieldEqualsIgnoreCaseSql, options.ResourceField, resource.ToString()))
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
