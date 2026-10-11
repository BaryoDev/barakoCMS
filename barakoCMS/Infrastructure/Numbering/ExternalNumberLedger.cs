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
        if (to < from)
            throw new ArgumentOutOfRangeException(nameof(to), $"'to' ({to}) is before 'from' ({from}).");
        if (to - from + 1 > MaxRange)
            throw new ArgumentOutOfRangeException(nameof(to), $"Ask for at most {MaxRange:N0} numbers at once.");

        var recorded = await session.Query<ExternalNumberUse>()
            .Where(x => x.Series == series && x.Number >= from && x.Number <= to)
            .Select(x => x.Number)
            .ToListAsync(ct);

        var seen = recorded.ToHashSet();
        var missing = new List<NumberRange>();
        long? start = null;
        for (var n = from; n <= to; n++)
        {
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

    // The tenant is part of the id, so the same number can be used once in each tenant.
    private static Guid IdFor(string tenantId, string series, long number)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\u001f{series}\u001f{number}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
