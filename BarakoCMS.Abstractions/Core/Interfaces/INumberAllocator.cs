using System.Globalization;
using System.Text.RegularExpressions;
using Marten;

namespace barakoCMS.Core.Interfaces;

/// <summary>When a series starts counting from 1 again.</summary>
public enum NumberReset
{
    Never,
    Yearly,
    Monthly,
    Daily,
}

/// <summary>
/// A named counter, for example queue tickets that restart every day or invoices that never do.
/// </summary>
/// <param name="Name">Lower case letters, digits, dots, dashes and underscores. Up to 64 characters.</param>
/// <param name="Prefix">Put in front of the formatted number.</param>
/// <param name="Padding">Minimum digits, 1 to 18. A wider value is never cut.</param>
/// <param name="Reset">When the count restarts.</param>
/// <param name="TimeZoneId">
/// IANA id whose local midnight starts a period, for example <c>Asia/Manila</c>. Null means UTC.
/// </param>
public sealed partial record NumberSeries(
    string Name,
    string Prefix = "",
    int Padding = 5,
    NumberReset Reset = NumberReset.Never,
    string? TimeZoneId = null)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}\\z")]
    private static partial Regex NamePattern();

    public void Validate()
    {
        if (Name is null || !NamePattern().IsMatch(Name))
            throw new ArgumentException(
                $"'{Name}' is not a valid series name. Use lower case letters, digits, dots, dashes and underscores, up to 64 characters.",
                nameof(Name));

        if (Padding is < 1 or > 18)
            throw new ArgumentException($"Padding must be 1 to 18, not {Padding}.", nameof(Padding));

        if (TimeZoneId is not null)
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new ArgumentException($"Time zone '{TimeZoneId}' is not known on this server.", nameof(TimeZoneId));
            }
        }
    }

    /// <summary>The period <paramref name="at"/> falls in, in the series' own time zone.</summary>
    public string PeriodFor(DateTimeOffset at)
    {
        var local = TimeZoneId is null
            ? at.ToUniversalTime()
            : TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId));

        return Reset switch
        {
            NumberReset.Never => string.Empty,
            NumberReset.Yearly => local.ToString("yyyy", CultureInfo.InvariantCulture),
            NumberReset.Monthly => local.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            NumberReset.Daily => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(Reset), Reset, null),
        };
    }

    public string Format(long value, string period)
    {
        var digits = value.ToString(CultureInfo.InvariantCulture).PadLeft(Padding, '0');
        return period.Length == 0 ? $"{Prefix}{digits}" : $"{Prefix}{period}-{digits}";
    }
}

public sealed record AllocatedNumber(string Series, string Period, long Value, string Formatted);

/// <summary>
/// Hands out the next number in a series with no gaps and no duplicates.
/// </summary>
/// <remarks>
/// The number is taken inside <paramref name="session"/>'s transaction and the counter row stays
/// locked until that session commits or is disposed. Commit and the number is used. Dispose without
/// saving and the next caller gets the same number, so a failed save never leaves a hole. The lock
/// also means writers on one series take turns: keep the session short.
/// </remarks>
public interface INumberAllocator
{
    Task<AllocatedNumber> NextAsync(IDocumentSession session, NumberSeries series, CancellationToken ct = default);

    /// <summary>
    /// The next number in the period <paramref name="at"/> falls in, for a number that belongs to a
    /// date other than today, such as a journal entry dated last December.
    /// </summary>
    /// <remarks>
    /// The default ignores <paramref name="at"/> and counts in today's period, which is what an
    /// implementor written before this member does.
    /// </remarks>
    Task<AllocatedNumber> NextAsync(IDocumentSession session, NumberSeries series, DateTimeOffset at, CancellationToken ct = default)
        => NextAsync(session, series, ct);
}

/// <summary>How a series hands out its numbers, and what each way costs.</summary>
public enum NumberStrategy
{
    /// <summary>
    /// No gaps and no duplicates. The number is taken inside the writer's transaction and the
    /// series' counter stays locked until it commits, so writers on one series take turns. For an
    /// invoice, where a gap has to be explained.
    /// </summary>
    Gapless = 0,

    /// <summary>
    /// No duplicates, in order, and gaps allowed: the number is taken and committed on its own, so a
    /// write that fails afterwards leaves its number unused. Writers wait only for the one statement.
    /// </summary>
    Sequence = 1,

    /// <summary>
    /// No duplicates, and gaps and order not kept: each instance takes a block of numbers at once and
    /// hands them out from memory, so a restart skips what was left of its block and two instances
    /// interleave. For a high write rate where a number only has to be unique.
    /// </summary>
    HiLo = 2,
}

/// <summary>The allocator for each <see cref="NumberStrategy"/>.</summary>
public interface INumberStrategies
{
    INumberAllocator For(NumberStrategy strategy);
}
