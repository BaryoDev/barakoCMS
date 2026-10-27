using System.Globalization;
using barakoCMS.Models;

namespace barakoCMS.Features.WorkflowRuns;

/// <summary>
/// A place in the list of runs ordered by (<see cref="WorkflowRun.UpdatedAt"/>, <see cref="WorkflowRun.Id"/>).
/// </summary>
/// <remarks>
/// Opaque to a client: it hands back what the last response gave it. The text form is the ticks of
/// the time and the id, then the ticks of <see cref="Anchor"/> when there is one, and a bare ISO
/// timestamp is accepted as the first cursor, standing for that time with the smallest id. A cursor
/// without the anchor part, the only form 5.3 issued, still reads.
///
/// The id is compared by the database in both the filter and the order, never in code, because
/// Postgres and .NET order a Guid differently and a cursor that disagrees with the order skips rows.
/// </remarks>
/// <param name="At">The UpdatedAt of the position.</param>
/// <param name="Id">The run at that position, or the smallest id for a bare time.</param>
/// <param name="Anchor">
/// Set while a client pages through runs changed inside <see cref="Margin"/>: the time the last page
/// of that run of pages sends it back to, so a run that committed late behind the pages it already
/// read is read on the next poll.
/// </param>
internal sealed record RunCursor(DateTimeOffset At, Guid Id, DateTimeOffset? Anchor = null)
{
    /// <summary>
    /// How far behind now a cursor is kept when the last page was taken. UpdatedAt is stamped before
    /// the transaction commits, so a slow one commits a stamp older than rows already read. Anything
    /// that commits within this of its stamp is seen on the next poll.
    /// </summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The filter for rows strictly after a cursor, with the time, the time again and the id as its
    /// three parameters. The first comparison repeats the row comparison's leading column: Postgres
    /// cannot start an index range from a row comparison, and can from this.
    /// </summary>
    public const string AfterSql =
        "public.mt_immutable_timestamptz(d.data ->> 'UpdatedAt') >= ? "
        + "AND (public.mt_immutable_timestamptz(d.data ->> 'UpdatedAt'), d.id) > (?, ?)";

    public override string ToString() => Anchor is { } anchor
        ? string.Create(CultureInfo.InvariantCulture, $"{At.UtcTicks}_{Id:N}_{anchor.UtcTicks}")
        : string.Create(CultureInfo.InvariantCulture, $"{At.UtcTicks}_{Id:N}");

    public static bool TryParse(string? text, out RunCursor cursor)
    {
        cursor = new RunCursor(DateTimeOffset.MinValue, Guid.Empty);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('_');
        if (parts.Length is 2 or 3
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTimeOffset.MaxValue.Ticks
            && Guid.TryParseExact(parts[1], "N", out var id))
        {
            DateTimeOffset? anchor = null;
            if (parts.Length == 3)
            {
                if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var anchorTicks)
                    || anchorTicks > DateTimeOffset.MaxValue.Ticks)
                {
                    return false;
                }

                anchor = new DateTimeOffset(anchorTicks, TimeSpan.Zero);
            }

            cursor = new RunCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id, anchor);
            return true;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            cursor = new RunCursor(at.ToUniversalTime(), Guid.Empty);
            return true;
        }

        return false;
    }

    /// <summary>Where to start polling after a response built at <paramref name="now"/>.</summary>
    public static RunCursor Start(DateTimeOffset now) => new(Floor(now - Margin), Guid.Empty);

    /// <summary>
    /// The cursor to send back after a page of changed runs, oldest first.
    /// </summary>
    /// <param name="input">The cursor the page was read from.</param>
    /// <param name="items">The page.</param>
    /// <param name="hasMore">Whether more changed runs follow this page.</param>
    /// <param name="now">The time the page was read.</param>
    /// <remarks>
    /// A page whose last run is older than <see cref="Margin"/> returns that run exactly. A last page
    /// that reaches inside the margin lags to <see cref="Margin"/> before now, so a run whose
    /// transaction commits late is seen on the next poll, or returns the input when that point is not
    /// past it.
    ///
    /// A page with more behind it always returns its last run, so a client reaches the end in pages
    /// and not at the speed of the clock. Lagging there instead sent every page back to the same runs
    /// at the front of the margin, and a run more than a page behind them was not reached until the
    /// clock passed it. When such a page reaches inside the margin the cursor records where the lag
    /// would have put it as <see cref="Anchor"/>, and the last page returns that, so a run committing
    /// late behind the pages is still read. An empty page returns the cursor it was given, or its
    /// anchor. A run read twice is harmless to a client that replaces by id; a run never read is not.
    /// </remarks>
    public static RunCursor Next(RunCursor input, IReadOnlyList<WorkflowRun> items, bool hasMore, DateTimeOffset now)
    {
        if (items.Count == 0) return input.Anchor is { } back ? new RunCursor(back, Guid.Empty) : input;

        var last = items[^1];
        var cap = Floor(now - Margin);
        if (hasMore)
        {
            var anchor = input.Anchor ?? (last.UpdatedAt <= cap ? null : cap > input.At ? cap : input.At);
            return new RunCursor(last.UpdatedAt, last.Id, anchor);
        }

        if (input.Anchor is { } start) return new RunCursor(start, Guid.Empty);
        if (last.UpdatedAt <= cap) return new RunCursor(last.UpdatedAt, last.Id);
        return cap > input.At ? new RunCursor(cap, Guid.Empty) : input;
    }

    private static DateTimeOffset Floor(DateTimeOffset at) =>
        new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
}
