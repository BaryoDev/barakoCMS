namespace barakoCMS.Models;

/// <summary>
/// A saved way of fetching rows a payload needs beyond the entry that triggered it.
/// </summary>
/// <remarks>
/// The triggering content is one blog post; "all subscribers" is not on it. Something has to fetch
/// that list without a developer writing a LINQ expression, or configuring rather than coding fails
/// on the second action of the first real workflow.
///
/// **This is not a query language, on purpose.** No SQL, no expression strings, no caller-supplied
/// predicates. A content type, typed filters, a sort and a limit. The moment it accepts an
/// expression it becomes an injection surface and an unbounded-cost surface at once, and the person
/// editing it is configuring a marketing workflow rather than writing a database query.
///
/// The obvious next asks are joining two content types and filtering on something computed. Both are
/// reasonable and both turn this into a query engine. The honest answer to either is a reporting
/// feature (#349), not growing this one, and it is written down here while the boundary is still
/// cheap to hold.
/// </remarks>
public class QueryDefinition
{
    /// <summary>The most rows a query may ever return, whatever it asks for.</summary>
    /// <remarks>
    /// A ceiling rather than a default. A query with no bound inside a workflow action is an
    /// accidental way to email everyone twice, and the operator who set the limit is not the person
    /// who finds out.
    /// </remarks>
    public const int MaxLimit = 1000;

    public const int DefaultLimit = 100;

    public Guid Id { get; set; }

    /// <summary>The admin's label, "Active newsletter subscribers".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What a request definition references. Unique per tenant.</summary>
    public string Slug { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public List<QueryFilter> Filters { get; set; } = new();

    public string? SortField { get; set; }

    public bool Descending { get; set; }

    public int Limit { get; set; } = DefaultLimit;

    /// <summary>
    /// The only fields that leave. An allowlist, not a convenience.
    /// </summary>
    /// <remarks>
    /// Combined with the Public-only rule, it means an operator has to name what goes out, and a
    /// schema change that adds a personal-data field later does not silently start including it in
    /// every payload that uses this query.
    /// </remarks>
    public List<string> Fields { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One typed comparison. Never an expression.</summary>
public class QueryFilter
{
    public string Field { get; set; } = string.Empty;

    /// <summary>eq, ne, lt, lte, gt, gte or contains.</summary>
    public string Op { get; set; } = "eq";

    public string Value { get; set; } = string.Empty;
}
