using System.Globalization;
using barakoCMS.Models;

namespace barakoCMS.Features.Audit.List;

/// <summary>
/// A place in the audit log ordered by (<see cref="AuditEvent.CreatedAt"/>, <see cref="AuditEvent.Id"/>).
/// </summary>
/// <remarks>
/// Opaque to a client: it hands back what the last response gave it. The text form is the ticks of
/// the time and the id, and a bare ISO timestamp is accepted as the first cursor, standing for that
/// time with the smallest id.
///
/// The id is compared by the database in both the filter and the order, never in code, because
/// Postgres and .NET order a Guid differently and a cursor that disagrees with the order skips rows.
/// </remarks>
internal sealed record AuditCursor(DateTimeOffset At, Guid Id)
{
    /// <summary>
    /// How far behind now a cursor is kept when the last page was taken. CreatedAt is stamped before
    /// the transaction commits, so a slow one commits a stamp older than rows already read. Anything
    /// that commits within this of its stamp is seen on the next poll.
    /// </summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The filter for rows strictly after a cursor, with the time, the time again and the id as its
    /// three parameters.
    /// </summary>
    public const string AfterSql =
        "public.mt_immutable_timestamptz(d.data ->> 'CreatedAt') >= ? "
        + "AND (public.mt_immutable_timestamptz(d.data ->> 'CreatedAt'), d.id) > (?, ?)";

    public override string ToString() => $"{At.UtcTicks.ToString(CultureInfo.InvariantCulture)}_{Id:N}";

    public static bool TryParse(string? text, out AuditCursor cursor)
    {
        cursor = new AuditCursor(DateTimeOffset.MinValue, Guid.Empty);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('_');
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTimeOffset.MaxValue.Ticks
            && Guid.TryParseExact(parts[1], "N", out var id))
        {
            cursor = new AuditCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            cursor = new AuditCursor(at.ToUniversalTime(), Guid.Empty);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The cursor to send back after a page of entries, oldest first.
    /// </summary>
    /// <param name="input">The cursor the page was read from.</param>
    /// <param name="items">The page.</param>
    /// <param name="hasMore">Whether more entries follow this page.</param>
    /// <param name="now">The time the page was read.</param>
    /// <remarks>
    /// An empty page returns the cursor it was given. A page whose last row is older than
    /// <see cref="Margin"/> returns that row exactly. Otherwise the cursor lags: it stops at
    /// <see cref="Margin"/> before now, so an entry whose transaction commits late is seen on the
    /// next poll. An entry read twice is harmless to a client that keys on <c>id</c>; an entry never
    /// read is not.
    ///
    /// A lagging cursor must get further than the one it replaces, or a client following it would
    /// read the same page for ever. When the margin point is not past the input, a page with more
    /// behind it returns its last row exactly, and the last page returns the input.
    /// </remarks>
    public static AuditCursor Next(AuditCursor input, IReadOnlyList<AuditEvent> items, bool hasMore, DateTimeOffset now)
    {
        if (items.Count == 0) return input;

        var last = items[^1];
        var lastAt = new DateTimeOffset(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
        var exact = new AuditCursor(ToMicroseconds(lastAt), last.Id);

        var cap = Floor(now - Margin);
        if (lastAt <= cap) return exact;
        if (cap > input.At) return new AuditCursor(cap, Guid.Empty);
        return hasMore ? exact : input;
    }

    /// <summary>
    /// Postgres keeps a timestamp to the microsecond and rounds the seventh digit of the stored text
    /// to get there, while a bound parameter is cut at the microsecond. A cursor left at 100 ns would
    /// sit just under the row it came from, and that row would be read again on every poll.
    /// </summary>
    private static DateTimeOffset ToMicroseconds(DateTimeOffset at)
    {
        var ticks = at.UtcTicks;
        var rest = ticks % 10;
        ticks -= rest;
        if (rest >= 5) ticks += 10;
        return new(ticks, TimeSpan.Zero);
    }

    private static DateTimeOffset Floor(DateTimeOffset at) =>
        new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
}
