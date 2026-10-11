using barakoCMS.Models;
using Marten;

namespace barakoCMS.Core.Interfaces;

public sealed record NumberRange(long From, long To);

public sealed record NumberGapReport(string Series, long From, long To, int Recorded, IReadOnlyList<NumberRange> Missing);

/// <summary>
/// Records numbers that something outside barakoCMS issued, and reports which numbers in a range
/// were never recorded.
/// </summary>
public interface IExternalNumberLedger
{
    /// <summary>
    /// Queues the record on <paramref name="session"/>. Recording the same number twice in one tenant
    /// fails when the session saves, with <c>JasperFx.DocumentAlreadyExistsException</c>.
    /// </summary>
    void Record(IDocumentSession session, string series, long number, string reference,
        ExternalNumberStatus status = ExternalNumberStatus.Issued);

    /// <summary>Numbers from <paramref name="from"/> to <paramref name="to"/>, inclusive, with no record. At most 100,000 at once.</summary>
    Task<NumberGapReport> GapsAsync(IQuerySession session, string series, long from, long to, CancellationToken ct = default);
}
