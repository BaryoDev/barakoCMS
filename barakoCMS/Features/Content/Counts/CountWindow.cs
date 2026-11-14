using System.Globalization;
using Marten;
using NpgsqlTypes;
using barakoCMS.Features.Queries.Run;

namespace barakoCMS.Features.Content.Counts;

/// <summary>
/// A time window for a count: <c>from</c> inclusive to <c>to</c> exclusive, optionally cut into
/// equal buckets. Shared by the content counts and the workflow run counts, so both take the same
/// parameters and refuse the same things.
/// </summary>
/// <remarks>
/// The window is read as a creation time. The previous window, the same length ending where this
/// one starts, is counted in the same query, which is how the delta is offered: as a field, not as a
/// second call.
/// </remarks>
internal sealed record CountWindow(DateTimeOffset From, DateTimeOffset To, TimeSpan? Bucket)
{
    /// <summary>The longest window, and so the furthest back a count reads (this one and the one before it).</summary>
    public const int MaxWindowDays = 90;

    /// <summary>The most buckets a series may have.</summary>
    public const int MaxBuckets = 200;

    private const int TimeoutSeconds = 30;

    /// <summary>The earliest <c>from</c> taken. Before it the previous window would run off the calendar.</summary>
    public static readonly DateTimeOffset Earliest = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// How far past a cap the window of a <c>from</c> with no <c>to</c> may drift between the
    /// validator reading the clock and the handler reading it again.
    /// </summary>
    public static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(1);

    public static readonly IReadOnlyList<string> Ranges = ["24h", "7d", "30d"];
    public static readonly IReadOnlyList<string> Series = ["hour", "day"];

    public TimeSpan Length => To - From;

    /// <summary>Where the previous window starts.</summary>
    public DateTimeOffset PreviousFrom => From - Length;

    /// <summary>The number of buckets of the series, zero when there is none.</summary>
    public int Buckets => Bucket is { } b ? Math.Min(MaxBuckets, (int)Math.Ceiling(Length / b)) : 0;

    public DateTimeOffset BucketStart(int index) => From + (Bucket ?? Length) * index;

    private static TimeSpan RangeLength(string range) => range switch
    {
        "24h" => TimeSpan.FromHours(24),
        "7d" => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(30),
    };

    /// <summary>
    /// The window the parameters name, or null when they name none (the count is then the one every
    /// caller had before), or the reason they cannot be used.
    /// </summary>
    /// <remarks>
    /// <c>from</c> and <c>to</c> are text so a value with no zone is read as UTC, not in the server's
    /// zone. <paramref name="slack"/> lets a window run that far past the caps, for a caller that
    /// resolves a second time with a later clock after the first read passed.
    /// </remarks>
    public static (CountWindow? Window, string? Refusal) Resolve(
        string? range, string? from, string? to, string? series, DateTimeOffset now, TimeSpan? slack = null)
    {
        TimeSpan? bucket = null;
        if (series is not null)
        {
            bucket = series switch
            {
                "hour" => TimeSpan.FromHours(1),
                "day" => TimeSpan.FromDays(1),
                _ => null,
            };
            if (bucket is null) return (null, $"Series must be one of: {string.Join(", ", Series)}.");
        }

        if (range is null && from is null && to is null)
        {
            return series is null ? (null, null) : (null, "Series needs a window: give range, or from.");
        }

        var allowed = slack ?? TimeSpan.Zero;
        DateTimeOffset start, end;
        if (range is not null)
        {
            if (!Ranges.Contains(range)) return (null, $"Range must be one of: {string.Join(", ", Ranges)}.");
            if (from is not null || to is not null) return (null, "Give range, or from and to, not both.");
            end = now;
            start = now - RangeLength(range);
        }
        else
        {
            if (from is null) return (null, "To needs a from.");
            if (!TryParse(from, out start)) return (null, "From must be a date and time such as 2026-03-10T00:00:00Z. No zone means UTC.");
            end = now;
            if (to is not null && !TryParse(to, out end)) return (null, "To must be a date and time such as 2026-03-13T00:00:00Z. No zone means UTC.");
            if (start < Earliest) return (null, $"From must be on or after {Earliest:yyyy-MM-dd}.");
        }

        if (end <= start) return (null, "From must be before to.");
        if (end - start - allowed > TimeSpan.FromDays(MaxWindowDays))
            return (null, $"The window is at most {MaxWindowDays} days.");

        var window = new CountWindow(start, end, bucket);
        if (bucket is { } size && (int)Math.Ceiling((window.Length - allowed) / size) > MaxBuckets)
            return (null, $"A series is at most {MaxBuckets} buckets. Use a shorter window or a coarser series.");

        return (window, null);
    }

    private static bool TryParse(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    /// <summary>
    /// How many rows of each status were created in the window and in the one before it, bucketed,
    /// in one query however many sources there are.
    /// </summary>
    /// <param name="session">The caller's session, so the tenant filter is Marten's.</param>
    /// <param name="sources">
    /// Queries already narrowed to the caller's rows and to creation times from <c>PreviousFrom</c>
    /// up to <c>To</c>, so the time index does that part.
    /// </param>
    /// <param name="window">The window.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<WindowCells> TallyAsync<T>(
        IQuerySession session, IReadOnlyList<IQueryable<T>> sources, CountWindow window, CancellationToken ct)
        where T : notnull
    {
        var cells = new Dictionary<(int Bucket, int Status), int>();
        if (sources.Count == 0) return new WindowCells(window, cells);

        var sql = new AggregateQuery.SqlBuilder();
        var from = sql.Add(window.From.UtcDateTime, NpgsqlDbType.TimestampTz);
        var size = sql.Add((window.Bucket ?? window.Length).TotalSeconds, NpgsqlDbType.Double);

        var parts = new List<string>();
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sql.Embed(sources[i].ToCommand(), $"w{i}");
            parts.Add("SELECT (d.data ->> 'Status')::int AS s, (d.data ->> 'CreatedAt')::timestamptz AS ts "
                + $"FROM ({source}) AS d");
        }

        sql.Command.CommandText =
            $"SELECT CASE WHEN t.ts < {from} THEN -1 ELSE floor(extract(epoch from (t.ts - {from})) / {size})::int END AS b, "
            + $"t.s, count(*) AS n FROM ({string.Join(" UNION ALL ", parts)}) AS t GROUP BY 1, 2";
        sql.Command.CommandTimeout = TimeoutSeconds;

        await using var command = sql.Command;
        await using var reader = await session.ExecuteReaderAsync(command, ct);
        while (await reader.ReadAsync(ct))
        {
            cells[(reader.GetInt32(0), reader.GetInt32(1))] = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
        }

        return new WindowCells(window, cells);
    }
}

/// <summary>Counts by bucket and status. Bucket -1 is the previous window, 0 and up the window.</summary>
internal sealed class WindowCells(CountWindow window, Dictionary<(int Bucket, int Status), int> cells)
{
    public CountWindow Window { get; } = window;

    /// <summary>The count of one status in the window.</summary>
    public int Current(int status) => cells.Where(c => c.Key.Bucket >= 0 && c.Key.Status == status).Sum(c => c.Value);

    /// <summary>The count of one status in the previous window.</summary>
    public int Previous(int status) => cells.TryGetValue((-1, status), out var n) ? n : 0;

    /// <summary>The count of one status in one bucket of the series.</summary>
    public int At(int bucket, int status) => cells.TryGetValue((bucket, status), out var n) ? n : 0;
}
