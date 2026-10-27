using System.Globalization;

namespace barakoCMS.Features.Activity;

/// <summary>
/// A place in the merged feed, ordered by (<see cref="At"/>, <see cref="Source"/>, then the order of
/// that source).
/// </summary>
/// <remarks>
/// Opaque to a client: it hands back what the last response gave it. The text form is the ticks of
/// the time, the source, the source's id and its sequence, then the ticks of <see cref="Anchor"/>
/// when there is one, and a bare ISO timestamp is accepted as the first cursor.
///
/// Each source orders its own rows by its own key, the audit log and runs by a Guid the database
/// compares and the event store by its own sequence, which the cursor names by stream and version, so the merge never compares two keys of different
/// sources. It orders by time, then by source, and keeps a source's own order inside that. A source
/// before the cursor's reads strictly after its time, the cursor's own source reads after its key,
/// and a source after it reads from its time, which is what makes the page boundary exact.
/// </remarks>
/// <param name="At">The time of the position.</param>
/// <param name="Source">The source the position belongs to, one of <see cref="ActivitySource"/>.</param>
/// <param name="Id">The source's id at that position, for the audit log, runs and bounces, and the stream id for the event store.</param>
/// <param name="Sequence">The version in the stream at that position, for the event store. Never the global event sequence, which counts every tenant's writes.</param>
/// <param name="Anchor">
/// Set while a client pages through rows newer than <see cref="Margin"/>: the time the last page of
/// that run sends it back to, so a row that committed late behind the pages it already read is read
/// on the next poll.
/// </param>
internal sealed record ActivityCursor(DateTimeOffset At, int Source, Guid Id, long Sequence, DateTimeOffset? Anchor = null)
{
    /// <summary>
    /// How far behind now a cursor is kept when the last page was taken. A row is stamped before its
    /// transaction commits, so a slow one commits a stamp older than rows already read. Anything
    /// that commits within this of its stamp is seen on the next poll.
    /// </summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

    /// <summary>The earliest time a cursor may name. Anything older is a malformed request, not a place in the feed.</summary>
    public static readonly DateTimeOffset Floor = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The time as Postgres holds it: whole microseconds, the seventh digit rounded. Every position read
    /// from Postgres goes through this, so the merge orders by the same value that source's SQL
    /// compares. Bounces keep their own time, since the bounce source compares that and not this.
    /// </summary>
    public static DateTimeOffset Micro(DateTimeOffset at) => new((at.UtcTicks + 5) / 10 * 10, TimeSpan.Zero);

    public override string ToString() => Anchor is { } anchor
        ? string.Create(CultureInfo.InvariantCulture, $"{At.UtcTicks}_{Source}_{Id:N}_{Sequence}_{anchor.UtcTicks}")
        : string.Create(CultureInfo.InvariantCulture, $"{At.UtcTicks}_{Source}_{Id:N}_{Sequence}");

    public static bool TryParse(string? text, out ActivityCursor cursor)
    {
        cursor = new ActivityCursor(DateTimeOffset.MinValue, 0, Guid.Empty, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('_');
        if (parts.Length is 4 or 5
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTimeOffset.MaxValue.Ticks
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var source)
            && source < ActivitySource.Count
            && Guid.TryParseExact(parts[2], "N", out var id)
            && long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            DateTimeOffset? anchor = null;
            if (parts.Length == 5)
            {
                if (!long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var anchorTicks)
                    || anchorTicks > DateTimeOffset.MaxValue.Ticks)
                {
                    return false;
                }

                anchor = new DateTimeOffset(anchorTicks, TimeSpan.Zero);
            }

            cursor = new ActivityCursor(new DateTimeOffset(ticks, TimeSpan.Zero), source, id, sequence, anchor);
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
    /// A page whose last position is older than <see cref="Margin"/> returns it exactly. A last page
    /// that reaches inside the margin lags to <see cref="Margin"/> before now, so a row that commits
    /// late is seen on the next poll, or returns the input when that point is not past it.
    ///
    /// A page with more behind it always returns its last position, so a client reaches the end in
    /// pages and not at the speed of the clock. Lagging there instead sent every page back to the same
    /// few rows at the front of the margin, and anything more than a page behind them was not reached
    /// until the clock passed it. When such a page reaches inside the margin the cursor records where
    /// the lag would have put it as <see cref="Anchor"/>, and the last page of the run returns that, so
    /// a row committing late behind the pages is still read. A row read twice is harmless to a client
    /// that replaces by id.
    /// </remarks>
    public static ActivityCursor Next(ActivityCursor input, ActivityCursor? last, bool hasMore, DateTimeOffset now)
    {
        if (last is null) return input.Anchor is { } back ? new ActivityCursor(back, 0, Guid.Empty, 0) : input;

        var cap = Trim(now - Margin);
        if (hasMore)
        {
            var anchor = input.Anchor ?? (last.At <= cap ? null : cap > input.At ? cap : input.At);
            return last with { Anchor = anchor };
        }

        if (input.Anchor is { } start) return new ActivityCursor(start, 0, Guid.Empty, 0);
        if (last.At <= cap) return last;
        return cap > input.At ? new ActivityCursor(cap, 0, Guid.Empty, 0) : input;
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
