using System.Linq.Expressions;
using Marten;

namespace barakoCMS.Infrastructure.Retention;

/// <summary>
/// What one document type keeps and for how long. The sweeper does the rest.
/// </summary>
internal abstract class RetentionDeclaration
{
    /// <summary>Names the declaration in logs.</summary>
    public abstract string Name { get; }

    /// <summary>Whether the session's partition holds any row this declaration could ever remove.</summary>
    public abstract Task<bool> HasRowsAsync(IQuerySession session, CancellationToken ct);

    /// <summary>Deletes what is past its window in the session's partition.</summary>
    /// <returns>How many rows were removed.</returns>
    public abstract Task<int> SweepAsync(
        IDocumentSession session, string tenantId, DateTimeOffset nowUtc, CancellationToken ct);
}

/// <summary>
/// A declaration over <typeparamref name="T"/>.
/// </summary>
/// <param name="Name">Names the declaration in logs.</param>
/// <param name="DueWhen">
/// Given the cutoff (now minus the window), the rows past their window. This is where the timestamp
/// member lives, so a type that ages on a fallback (completed, else created) says so here.
/// </param>
/// <param name="WindowFor">
/// The window for a tenant. Null, zero or negative keeps the rows forever, the reading every
/// retention window here settled on because "0" also reads as "delete now" and keeping is the
/// mistake that can be recovered from.
/// </param>
/// <param name="Candidates">
/// The rows worth visiting a partition for, used to skip empty partitions. Null means any row of the type.
/// </param>
internal sealed class RetentionDeclaration<T>(
    string Name,
    Func<DateTimeOffset, Expression<Func<T, bool>>> DueWhen,
    Func<string, TimeSpan?> WindowFor,
    Expression<Func<T, bool>>? Candidates = null) : RetentionDeclaration
    where T : notnull
{
    public override string Name { get; } = Name;

    public override Task<bool> HasRowsAsync(IQuerySession session, CancellationToken ct) =>
        Candidates is null
            ? session.Query<T>().AnyAsync(ct)
            : session.Query<T>().AnyAsync(Candidates, ct);

    public override async Task<int> SweepAsync(
        IDocumentSession session, string tenantId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var window = WindowFor(tenantId);
        if (window is null || window.Value <= TimeSpan.Zero) return 0;

        var due = DueWhen(nowUtc - window.Value);

        var count = await session.Query<T>().CountAsync(due, ct);
        if (count == 0) return 0;

        session.DeleteWhere(due);
        await session.SaveChangesAsync(ct);

        return count;
    }
}
