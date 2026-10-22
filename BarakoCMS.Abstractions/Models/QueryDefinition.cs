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

    /// <summary>The date window an aggregate counts inside, or null for every entry.</summary>
    public QueryRange? Range { get; set; }

    /// <summary>What an aggregate groups by. Null or empty for a query that returns rows.</summary>
    public List<QueryGroup>? GroupBy { get; set; }

    /// <summary>What an aggregate computes per group. A query with any is an aggregate.</summary>
    public List<QueryMeasure>? Measures { get; set; }

    /// <summary>True when the query computes figures in the database instead of returning rows.</summary>
    public bool IsAggregate => Measures is { Count: > 0 };

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the query was deleted, or null while it is live. A deleted query is invisible to every
    /// reader but the restore and erase routes, and still holds its slug.
    /// </summary>
    public DateTime? DeletedAt { get; set; }
}

/// <summary>One typed comparison. Never an expression.</summary>
public class QueryFilter
{
    public string Field { get; set; } = string.Empty;

    /// <summary>eq, ne, lt, lte, gt, gte or contains.</summary>
    public string Op { get; set; } = "eq";

    public string Value { get; set; } = string.Empty;
}

/// <summary>A date field and the window it must fall in. A relative window or fixed bounds, not both.</summary>
public class QueryRange
{
    public string Field { get; set; } = string.Empty;

    /// <summary>today, last7Days, last30Days, thisMonth, lastMonth or thisYear.</summary>
    public string? Relative { get; set; }

    /// <summary>First day, as yyyy-MM-dd, in the tenant's time zone.</summary>
    public string? From { get; set; }

    /// <summary>Last day, as yyyy-MM-dd, in the tenant's time zone.</summary>
    public string? To { get; set; }
}

/// <summary>One grouping column. A bucket turns a date field into day, month or year.</summary>
public class QueryGroup
{
    public string Field { get; set; } = string.Empty;

    public string? Bucket { get; set; }
}

/// <summary>One figure per group: sum, count, avg, min or max of a field, or of a field one reference away.</summary>
public class QueryMeasure
{
    public string Fn { get; set; } = string.Empty;

    /// <summary>Null for a plain count of entries.</summary>
    public string? Field { get; set; }

    /// <summary>The name the figure carries in a row.</summary>
    public string As { get; set; } = string.Empty;

    /// <summary>A reference field on the queried type. The measure is taken over the entries it names.</summary>
    public string? Via { get; set; }
}
