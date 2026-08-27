using barakoCMS.Models;

namespace barakoCMS.Features.Public;

public enum FilterOp { Eq, Ne, Lt, Lte, Gt, Gte, Contains }

/// <summary>One validated field comparison, safe to translate into SQL.</summary>
/// <param name="Field">A field name the content type marks Public. Never caller-supplied text.</param>
/// <param name="Type">
/// The field's declared type. Carried because jsonb compares by type first: without it a filter on
/// a string field holding "500" would emit the number 500 and match nothing.
/// </param>
public readonly record struct DeliveryFilter(string Field, FilterOp Op, string Value, string Type);

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
            .ToDictionary(f => f.Name, f => f.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase);

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

                if (!allowed.ContainsKey(field))
                    return new DeliveryQuery { Error = Unsortable(field, allowed) };

                sort = new DeliverySort(Canonical(allowed, field), descending);
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

            if (!allowed.TryGetValue(name, out var declaredType))
                return new DeliveryQuery { Error = Unfilterable(name, allowed) };
            var canonical = Canonical(allowed, name);

            if (!Ops.TryGetValue(op, out var parsedOp))
                return new DeliveryQuery
                {
                    Error = $"Unknown operator '{op}'. Supported: {string.Join(", ", Ops.Keys)}.",
                };

            // The canonical name from the schema is stored, never the caller's spelling, so what
            // reaches the query builder can only be a string the content type already declared.
            filters.Add(new DeliveryFilter(canonical, parsedOp, rawValue ?? string.Empty, declaredType));

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
        // #>> '{}' unwraps a jsonb scalar to text without its quotes, so a stored "hat" compares
        // as hat rather than "hat".
        if (f.Op == FilterOp.Contains)
            return ($"({KeyLookup} #>> '{{}}') ILIKE ?", [f.Field, $"%{Escape(f.Value)}%"]);

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

        return ($"{KeyLookup} {op} ?::jsonb", [f.Field, JsonLiteral(f.Value, f.Type)]);
    }

    /// <summary>
    /// Finds a field's value by key, case-insensitively, the way the public projection does.
    /// </summary>
    /// <remarks>
    /// <c>ToPublic</c> matches the schema with <c>OrdinalIgnoreCase</c>, so a record holding
    /// "price" under a schema field named "Price" is delivered normally. PostgreSQL's <c>-&gt;</c>
    /// is case sensitive, so a filter built from the schema spelling would miss that record: it
    /// would appear in an unfiltered list and vanish from a filtered one, reading as "no matches"
    /// rather than as a fault. Delivery and filtering have to agree on what a key is.
    ///
    /// The cost is that this cannot use an expression index on the key, so a filtered list scans.
    /// Indexing strategy is deferred to the sorting half of #140; correctness comes first, and a
    /// fast wrong answer is not worth having.
    /// </remarks>
    private const string KeyLookup =
        "(SELECT e.value FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower(?) LIMIT 1)";

    /// <summary>
    /// The value as a JSON scalar: a number bare when the field is declared numeric, quoted
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// jsonb compares by type first, so a number stored as <c>500</c> never matches the string
    /// <c>"500"</c>. Emitting a numeric-looking value as a number is what makes a price filter work
    /// at all, and it is safe because the result is a JSON literal, not SQL: it travels as a bound
    /// parameter and is cast to jsonb by Postgres.
    /// </remarks>
    private static string JsonLiteral(string value, string declaredType)
    {
        // The schema decides, not the shape of the text. Guessing from the digits alone turns
        // filter[Title][eq]=500 on a string field into the number 500, which never equals the
        // stored string "500", and the caller sees an empty result rather than an error.
        if (barakoCMS.Core.Validation.FieldTypeRegistry.IsNumericType(declaredType)
            && decimal.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            return n.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (string.Equals(declaredType, "bool", StringComparison.OrdinalIgnoreCase)
            && bool.TryParse(value, out var b))
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
    /// <summary>The schema's own spelling of a field, whatever casing the caller used.</summary>
    private static string Canonical(Dictionary<string, string> allowed, string name) =>
        allowed.Keys.First(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    private static string Unfilterable(string field, Dictionary<string, string> allowed) =>
        $"Field '{field}' is not filterable. Filterable fields: {Names(allowed)}.";

    private static string Unsortable(string field, Dictionary<string, string> allowed) =>
        $"Field '{field}' is not sortable. Sortable fields: {Names(allowed)}.";

    private static string Names(Dictionary<string, string> allowed) =>
        allowed.Count == 0 ? "(none)" : string.Join(", ", allowed.Keys.OrderBy(x => x, StringComparer.Ordinal));
}
