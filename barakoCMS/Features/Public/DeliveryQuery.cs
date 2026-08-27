using barakoCMS.Models;

namespace barakoCMS.Features.Public;

public enum FilterOp { Eq, Ne, Lt, Lte, Gt, Gte, Contains }

/// <summary>One validated field comparison, safe to translate into SQL.</summary>
/// <param name="Field">A field name the content type marks Public. Never caller-supplied text.</param>
public readonly record struct DeliveryFilter(string Field, FilterOp Op, string Value);

/// <summary>A validated sort, or none.</summary>
public readonly record struct DeliverySort(string Field, bool Descending);

/// <summary>The outcome of parsing the query string: either a query to run, or the reason it was refused.</summary>
public sealed class DeliveryQuery
{
    public const int MaxFilters = 5;

    public IReadOnlyList<DeliveryFilter> Filters { get; init; } = Array.Empty<DeliveryFilter>();
    public DeliverySort? Sort { get; init; }
    public string? Error { get; init; }

    public bool IsValid => Error is null;

    private static readonly Dictionary<string, FilterOp> Ops = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eq"] = FilterOp.Eq,
        ["ne"] = FilterOp.Ne,
        ["lt"] = FilterOp.Lt,
        ["lte"] = FilterOp.Lte,
        ["gt"] = FilterOp.Gt,
        ["gte"] = FilterOp.Gte,
        ["contains"] = FilterOp.Contains,
    };

    /// <summary>
    /// Parses <c>filter[field][op]=value</c> and <c>sort=field</c> / <c>sort=-field</c> against the
    /// fields a content type actually exposes.
    /// </summary>
    /// <remarks>
    /// The allowlist is built the same way <see cref="PublicDelivery.ToPublic"/> builds it, from
    /// fields marked <see cref="SensitivityLevel.Public"/>. That is deliberate and load-bearing:
    /// filtering on a field the caller cannot read is an oracle. A caller could binary-search a
    /// Sensitive salary or date of birth by observing which entries come back, without the value ever
    /// appearing in a response. Refusing unknown fields rather than ignoring them matters for the
    /// same reason a silently ignored filter returns more rows than the caller asked for, and the
    /// caller cannot tell the difference between "no filter" and "no matches".
    /// </remarks>
    public static DeliveryQuery Parse(
        IEnumerable<KeyValuePair<string, string?>> query, ContentTypeDefinition? def)
    {
        if (def is null)
            return new DeliveryQuery { Error = "Unknown content type." };

        var allowed = def.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filters = new List<DeliveryFilter>();
        DeliverySort? sort = null;

        foreach (var (rawKey, rawValue) in query)
        {
            if (string.Equals(rawKey, "sort", StringComparison.OrdinalIgnoreCase))
            {
                var value = rawValue?.Trim();
                if (string.IsNullOrEmpty(value))
                    continue;

                var descending = value.StartsWith('-');
                var field = descending ? value[1..] : value;

                if (!allowed.TryGetValue(field, out var canonicalSort))
                    return new DeliveryQuery { Error = Unsortable(field, allowed) };

                sort = new DeliverySort(canonicalSort, descending);
                continue;
            }

            if (!rawKey.StartsWith("filter[", StringComparison.OrdinalIgnoreCase))
                continue;

            // filter[field][op]
            var parts = rawKey[7..].TrimEnd(']').Split("][", StringSplitOptions.None);
            if (parts.Length != 2)
                return new DeliveryQuery
                {
                    Error = $"Filter '{rawKey}' is malformed. Expected filter[field][op]=value.",
                };

            var (name, op) = (parts[0], parts[1]);

            if (!allowed.TryGetValue(name, out var canonical))
                return new DeliveryQuery { Error = Unfilterable(name, allowed) };

            if (!Ops.TryGetValue(op, out var parsedOp))
                return new DeliveryQuery
                {
                    Error = $"Unknown operator '{op}'. Supported: {string.Join(", ", Ops.Keys)}.",
                };

            // The canonical name from the schema is stored, never the caller's spelling, so what
            // reaches the query builder can only be a string the content type already declared.
            filters.Add(new DeliveryFilter(canonical, parsedOp, rawValue ?? string.Empty));

            // Arbitrary filter combinations against a JSONB column on an anonymous endpoint is a
            // denial-of-service surface, so the count is capped rather than left to the caller.
            if (filters.Count > MaxFilters)
                return new DeliveryQuery
                {
                    Error = $"Too many filters. At most {MaxFilters} are allowed per request.",
                };
        }

        return new DeliveryQuery { Filters = filters, Sort = sort };
    }

    /// <summary>
    /// One filter as a SQL fragment plus its bound parameters, for Marten's <c>MatchesSql</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not JSONPath. The <c>@?</c> JSONPath operator contains a <c>?</c>, which is also
    /// MatchesSql's parameter placeholder, and the overload that exists to resolve that collision
    /// (<c>MatchesJsonPath</c>) could not bind parameters at all until JasperFx/marten#5289 and
    /// #5293, both of which landed in Marten 9.30. This project is on 8.37, and crossing a major
    /// version to reach a four-line fix is a worse trade than not needing it.
    ///
    /// Extracting with <c>-&gt;</c> avoids <c>?</c> entirely, so ordinary parameters work: the field
    /// name and the value are both bound, and neither reaches the SQL text. That is stronger than
    /// the JSONPath version would have been, where only the value could be bound.
    ///
    /// Comparison happens in jsonb rather than text, so 9 sorts below 10 instead of after it.
    /// </remarks>
    public static (string Sql, object[] Parameters) ToSql(DeliveryFilter f)
    {
        // ILIKE on the text projection: a substring match is a text question, and asking it of a
        // jsonb value would compare the quotes too.
        if (f.Op == FilterOp.Contains)
            return ("(d.data -> 'Data' ->> ?) ILIKE ?", [f.Field, $"%{Escape(f.Value)}%"]);

        var op = f.Op switch
        {
            FilterOp.Eq => "=",
            FilterOp.Ne => "<>",
            FilterOp.Lt => "<",
            FilterOp.Lte => "<=",
            FilterOp.Gt => ">",
            FilterOp.Gte => ">=",
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };

        return ($"(d.data -> 'Data' -> ?) {op} ?::jsonb", [f.Field, JsonLiteral(f.Value)]);
    }

    /// <summary>
    /// The value as a JSON scalar: a number bare, anything else quoted.
    /// </summary>
    /// <remarks>
    /// jsonb compares by type first, so a number stored as <c>500</c> never matches the string
    /// <c>"500"</c>. Emitting a numeric-looking value as a number is what makes a price filter work
    /// at all, and it is safe because the result is a JSON literal, not SQL: it travels as a bound
    /// parameter and is cast to jsonb by Postgres.
    /// </remarks>
    private static string JsonLiteral(string value)
    {
        if (decimal.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            return n.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (bool.TryParse(value, out var b))
            return b ? "true" : "false";

        return System.Text.Json.JsonSerializer.Serialize(value);
    }

    /// <summary>Neutralises the LIKE wildcards so a search for "50%" means "50%" and not "50 anything".</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// Names the fields that <em>are</em> filterable, so a caller who asked for a Sensitive field
    /// cannot tell it apart from one that does not exist. Both answers are "not in this list".
    /// </summary>
    private static string Unfilterable(string field, HashSet<string> allowed) =>
        $"Field '{field}' is not filterable. Filterable fields: {Names(allowed)}.";

    private static string Unsortable(string field, HashSet<string> allowed) =>
        $"Field '{field}' is not sortable. Sortable fields: {Names(allowed)}.";

    private static string Names(HashSet<string> allowed) =>
        allowed.Count == 0 ? "(none)" : string.Join(", ", allowed.OrderBy(x => x, StringComparer.Ordinal));
}
