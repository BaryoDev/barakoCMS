namespace barakoCMS.Core.Interfaces;

/// <summary>
/// A place failures are recorded, read by the Diagnostics module's incidents view (#893), which
/// groups what every source returns by fingerprint and redacts it before anything leaves the API.
/// </summary>
/// <remarks>
/// <para>
/// Register an implementation as a scoped <see cref="IIncidentSource"/> and the view reads it. A
/// source returns occurrences as recorded and does not redact: the view redacts every source the
/// same way, so one source cannot be the hole another closed.
/// </para>
/// <para>
/// A source only returns what the caller could already read elsewhere.
/// <see cref="RequiredCapability"/> names the capability that reads the underlying records, and the
/// view leaves the source out for a caller who does not hold it.
/// </para>
/// </remarks>
public interface IIncidentSource
{
    /// <summary>A short, stable name, for example <c>client-errors</c>. The view filters by it.</summary>
    string Name { get; }

    /// <summary>The capability that reads this source's records, or null when the view's own gate is enough.</summary>
    string? RequiredCapability { get; }

    /// <summary>
    /// The newest occurrences first, at most <see cref="IncidentQuery.Limit"/> of them, none older
    /// than <see cref="IncidentQuery.Since"/>.
    /// </summary>
    Task<IReadOnlyList<IncidentOccurrence>> ReadAsync(IncidentQuery query, CancellationToken ct);
}

/// <summary>What the incidents view asks a source for.</summary>
/// <param name="Tenant">The tenant the request resolved to.</param>
/// <param name="AllTenants">
/// True when the caller holds the view's capability through a global role. A source whose records
/// carry their tenant as data may then return every tenant's; one stored per tenant returns the
/// current tenant's either way.
/// </param>
/// <param name="Since">The oldest occurrence wanted.</param>
/// <param name="Limit">The most occurrences to return.</param>
public sealed record IncidentQuery(string Tenant, bool AllTenants, DateTimeOffset Since, int Limit);

/// <summary>One recorded failure, or one row that already counts several.</summary>
public sealed class IncidentOccurrence
{
    /// <summary>Equal for occurrences of the same fault, so the view can group them.</summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>One line saying what failed. Redacted by the view, not by the source.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Where or why, when there is more to say. Redacted by the view.</summary>
    public string? Detail { get; init; }

    public string? Tenant { get; init; }

    /// <summary>The release that produced it, when the source knows one.</summary>
    public string? Release { get; init; }

    /// <summary>How many times this row has happened. At least one.</summary>
    public int Count { get; init; } = 1;

    public DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }

    public bool Resolved { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public string? ResolvedBy { get; init; }
    public string? ResolutionReference { get; init; }
    public string? ResolutionNote { get; init; }
}
