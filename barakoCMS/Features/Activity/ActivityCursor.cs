using System.Globalization;

namespace barakoCMS.Features.Activity;

/// <summary>
/// A place in the merged feed, ordered by (<see cref="At"/>, <see cref="Source"/>, then the order of
/// that source).
/// </summary>
/// <remarks>
/// Opaque to a client: it hands back what the last response gave it. The text form is the ticks of
/// the time, the source, the source's id and its sequence, and a bare ISO timestamp is accepted as
/// the first cursor.
///
/// Each source orders its own rows by its own key, the audit log and runs by a Guid the database
/// compares and the event store by sequence, so the merge never compares two keys of different
/// sources. It orders by time, then by source, and keeps a source's own order inside that. A source
/// before the cursor's reads strictly after its time, the cursor's own source reads after its key,
/// and a source after it reads from its time, which is what makes the page boundary exact.
/// </remarks>
internal sealed record ActivityCursor(DateTimeOffset At, int Source, Guid Id, long Sequence)
{
    /// <summary>
    /// How far behind now a cursor is kept when the last page was taken. A row is stamped before its
    /// transaction commits, so a slow one commits a stamp older than rows already read. Anything
    /// that commits within this of its stamp is seen on the next poll.
    /// </summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

    /// <summary>The earliest time a cursor may name. Anything older is a malformed request, not a place in the feed.</summary>
    public static readonly DateTimeOffset Floor = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{At.UtcTicks}_{Source}_{Id:N}_{Sequence}");

    public static bool TryParse(string? text, out ActivityCursor cursor)
    {
        cursor = new ActivityCursor(DateTimeOffset.MinValue, 0, Guid.Empty, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('_');
        if (parts.Length == 4
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTimeOffset.MaxValue.Ticks
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var source)
            && source < ActivitySource.Count
            && Guid.TryParseExact(parts[2], "N", out var id)
            && long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            cursor = new ActivityCursor(new DateTimeOffset(ticks, TimeSpan.Zero), source, id, sequence);
            return true;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            cursor = new ActivityCursor(at.ToUniversalTime(), 0, Guid.Empty, 0);
            return true;
        }

        return false;
    }

    /// <summary>Where to start polling after a newest-first first page built at <paramref name="now"/>.</summary>
    public static ActivityCursor Start(DateTimeOffset now) => new(Trim(now - Margin), 0, Guid.Empty, 0);

    /// <summary>
    /// The cursor to send back after a forward page.
    /// </summary>
    /// <param name="input">The cursor the page was read from.</param>
    /// <param name="last">The last position the page consumed, whether or not it became a row, or null for an empty page.</param>
    /// <param name="hasMore">Whether more follows this page.</param>
    /// <param name="now">The time the page was read.</param>
    /// <remarks>
    /// The same rule as the workflow run cursor. A page whose last position is older than
    /// <see cref="Margin"/> returns it exactly. Otherwise the cursor lags to <see cref="Margin"/>
    /// before now, so a row that commits late is seen on the next poll. A lagging cursor must get
    /// further than the one it replaces or a client would read the same page for ever: when the
    /// margin point is not past the input, a page with more behind it returns its last position and
    /// the last page returns the input. A row read twice is harmless to a client that replaces by id.
    /// </remarks>
    public static ActivityCursor Next(ActivityCursor input, ActivityCursor? last, bool hasMore, DateTimeOffset now)
    {
        if (last is null) return input;

        var cap = Trim(now - Margin);
        if (last.At <= cap) return last;
        if (cap > input.At) return new ActivityCursor(cap, 0, Guid.Empty, 0);
        return hasMore ? last : input;
    }

    private static DateTimeOffset Trim(DateTimeOffset at) =>
        new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
}

/// <summary>The sources of the feed, in the order the merge ranks them at the same instant.</summary>
internal static class ActivitySource
{
    public const int Audit = 0;
    public const int Runs = 1;
    public const int Events = 2;
    public const int Bounces = 3;
    public const int Count = 4;
}
