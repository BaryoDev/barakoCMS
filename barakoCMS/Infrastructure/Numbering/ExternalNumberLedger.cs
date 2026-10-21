using System.Security.Cryptography;
using System.Text;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Numbering;

internal sealed class ExternalNumberLedger(TimeProvider clock) : IExternalNumberLedger
{
    public const int MaxRange = 100_000;

    public void Record(IDocumentSession session, string series, long number, string reference,
        ExternalNumberStatus status = ExternalNumberStatus.Issued)
    {
        ArgumentNullException.ThrowIfNull(session);
        new NumberSeries(series).Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(number);

        // Insert, not Store: Store would quietly overwrite the first use of a number.
        session.Insert(new ExternalNumberUse
        {
            Id = IdFor(session.TenantId, series, number),
            Series = series,
            Number = number,
            Reference = reference ?? string.Empty,
            Status = status,
            RecordedAt = clock.GetUtcNow(),
        });
    }

    public async Task<NumberGapReport> GapsAsync(IQuerySession session, string series, long from, long to, CancellationToken ct = default)
    {
        ValidateRange(series, from, to);

        var recorded = await session.Query<ExternalNumberUse>()
            .Where(x => x.Series == series && x.Number >= from && x.Number <= to)
            .Select(x => x.Number)
            .ToListAsync(ct);

        var seen = recorded.ToHashSet();
        var missing = new List<NumberRange>();
        long? start = null;
        // Walk an offset, not the number itself: with to == long.MaxValue, n++ would wrap and never end.
        for (long i = 0, len = to - from; i <= len; i++)
        {
            var n = from + i;
            if (!seen.Contains(n))
            {
                start ??= n;
                continue;
            }

            if (start is { } s)
            {
                missing.Add(new NumberRange(s, n - 1));
                start = null;
            }
        }

        if (start is { } tail)
            missing.Add(new NumberRange(tail, to));

        return new NumberGapReport(series, from, to, seen.Count, missing);
    }

    /// <summary>The one place a gap request is checked. Throws <see cref="ArgumentException"/>.</summary>
    internal static void ValidateRange(string series, long from, long to)
    {
        new NumberSeries(series).Validate();
        if (RangeProblem(from, to) is { } problem)
            throw new ArgumentOutOfRangeException(nameof(to), problem);
    }

    /// <summary>
    /// What is wrong with a range, or null. Numbers are non-negative, which also keeps
    /// <c>to - from</c> from overflowing once <c>to &gt;= from</c>.
    /// </summary>
    internal static string? RangeProblem(long from, long to)
    {
        if (from < 0)
            return $"'from' ({from}) must not be negative.";
        if (to < from)
            return $"'to' ({to}) is before 'from' ({from}).";
        if (to - from >= MaxRange)
            return $"Ask for at most {MaxRange:N0} numbers at once.";
        return null;
    }

    // The tenant is part of the id, so the same number can be used once in each tenant.
    // Record and the already-issued check read it through here, so they cannot disagree on the key.
    internal static Guid IdFor(string tenantId, string series, long number)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\u001f{series}\u001f{number}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
