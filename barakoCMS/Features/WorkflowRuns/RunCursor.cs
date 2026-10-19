using System.Globalization;
using barakoCMS.Models;

namespace barakoCMS.Features.WorkflowRuns;

/// <summary>
/// A place in the list of runs ordered by (<see cref="WorkflowRun.UpdatedAt"/>, <see cref="WorkflowRun.Id"/>).
/// </summary>
/// <remarks>
/// Opaque to a client: it hands back what the last response gave it. The text form is the ticks of
/// the time and the id, and a bare ISO timestamp is accepted as the first cursor, standing for that
/// time with the smallest id.
///
/// The id is compared by the database in both the filter and the order, never in code, because
/// Postgres and .NET order a Guid differently and a cursor that disagrees with the order skips rows.
/// </remarks>
internal sealed record RunCursor(DateTimeOffset At, Guid Id)
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

    public override string ToString() => $"{At.UtcTicks.ToString(CultureInfo.InvariantCulture)}_{Id:N}";

    public static bool TryParse(string? text, out RunCursor cursor)
    {
        cursor = new RunCursor(DateTimeOffset.MinValue, Guid.Empty);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('_');
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTimeOffset.MaxValue.Ticks
            && Guid.TryParseExact(parts[1], "N", out var id))
        {
            cursor = new RunCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
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
    /// An empty page returns the cursor it was given. A page with more behind it returns its last
    /// row exactly, since lagging there would read the same rows again and never get further. The
    /// last page lags: the cursor stops at <see cref="Margin"/> before now, so a run whose
    /// transaction commits late is seen on the next poll. A run read twice is harmless to a client
    /// that replaces by id; a run never read is not.
    /// </remarks>
    public static RunCursor Next(RunCursor input, IReadOnlyList<WorkflowRun> items, bool hasMore, DateTimeOffset now)
    {
        if (items.Count == 0) return input;

        var last = items[^1];
        var exact = new RunCursor(last.UpdatedAt, last.Id);
        if (hasMore) return exact;

        var cap = Floor(now - Margin);
        if (last.UpdatedAt <= cap) return exact;
        return cap <= input.At ? input : new RunCursor(cap, Guid.Empty);
    }

    private static DateTimeOffset Floor(DateTimeOffset at) =>
        new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
}
