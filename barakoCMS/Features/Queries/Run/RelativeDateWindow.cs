namespace barakoCMS.Features.Queries.Run;

/// <summary>
/// The relative day words a range or a rollup window takes (<c>thisMonth</c> and its kin), and the
/// days and UTC bounds each one stands for in a time zone. One list and one resolution, so a saved
/// query, a query written as text and a rollup window agree on what a word means.
/// </summary>
internal static class RelativeDateWindow
{
    /// <summary>The words, in the casing documentation and messages use.</summary>
    public static readonly IReadOnlyList<string> Words = ["today", "last7Days", "last30Days", "thisMonth", "lastMonth", "thisYear"];

    /// <summary>Whether <paramref name="text"/> is one of <see cref="Words"/>, ignoring case and surrounding space.</summary>
    public static bool IsWord(string? text) =>
        text is not null && Words.Contains(text.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The word in the casing of <see cref="Words"/>, or null when it is not one.</summary>
    public static string? Canonical(string? text) =>
        text is null ? null : Words.FirstOrDefault(w => string.Equals(w, text.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The first and last day, inclusive, that <paramref name="word"/> covers when today is <paramref name="today"/>.</summary>
    /// <exception cref="ArgumentException">The text is not one of <see cref="Words"/>.</exception>
    public static (DateOnly From, DateOnly To) Resolve(string word, DateOnly today)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return Canonical(word)?.ToLowerInvariant() switch
        {
            "today" => (today, today),
            "last7days" => (today.AddDays(-6), today),
            "last30days" => (today.AddDays(-29), today),
            "thismonth" => (monthStart, monthStart.AddMonths(1).AddDays(-1)),
            "lastmonth" => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            "thisyear" => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
            _ => throw new ArgumentException($"'{word}' is not one of {string.Join(", ", Words)}.", nameof(word)),
        };
    }

    /// <summary>
    /// A local time as UTC. A local midnight that a change to summer time skips does not exist, so it
    /// is read as the first valid instant after it instead of throwing.
    /// </summary>
    public static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
    }

    /// <summary>
    /// The UTC wall times of the local midnight that starts <paramref name="from"/> and of the one
    /// after <paramref name="to"/>. Both are <see cref="DateTimeKind.Unspecified"/>, so they bind as
    /// <c>timestamp</c>, the type Marten's CreatedAt index is on.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtcExclusive) UtcBounds(DateOnly from, DateOnly to, TimeZoneInfo zone) =>
        (DateTime.SpecifyKind(ToUtc(from.ToDateTime(TimeOnly.MinValue), zone), DateTimeKind.Unspecified),
         DateTime.SpecifyKind(ToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), zone), DateTimeKind.Unspecified));
}
