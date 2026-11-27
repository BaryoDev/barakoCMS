using barakoCMS.Features.Public;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Infrastructure.Connectors;

/// <summary>Rows a query returned, or the reason it did not run.</summary>
public sealed record QueryResult(IReadOnlyList<Dictionary<string, object>> Rows, string? Refusal)
{
    public bool Ok => Refusal is null;
    public int Count => Rows.Count;

    /// <summary>The ids of the entries the rows came from, in row order. Empty for a run that refused.</summary>
    public IReadOnlyList<Guid> Ids { get; init; } = [];

    public static QueryResult Refused(string reason) => new([], reason);
}

public interface IQueryRunner
{
    /// <summary>Runs a saved query and returns only the fields it names.</summary>
    Task<QueryResult> RunAsync(QueryDefinition definition, CancellationToken ct);

    /// <summary>Why this definition may not be saved or run, or null.</summary>
    Task<string?> ValidateAsync(QueryDefinition definition, CancellationToken ct);
}

/// <summary>
/// What one run of a saved query is given beyond the definition: the values its parameters take.
/// </summary>
/// <param name="Values">
/// The value of each parameter by its name without the <c>@</c>. Null when there are none to give,
/// and then a filter that names a parameter refuses.
/// </param>
internal sealed record QueryRunContext(IReadOnlyDictionary<string, string>? Values);

/// <summary>
/// The run a workflow step and the queries API use: parameters are bound as values and nothing in
/// the text of a query is read as anything else.
/// </summary>
internal interface IContextQueryRunner
{
    Task<QueryResult> RunAsync(QueryDefinition definition, QueryRunContext context, CancellationToken ct);

    Task<string?> ValidateAsync(QueryDefinition definition, QueryRunContext context, CancellationToken ct);
}

internal sealed class QueryRunner(IQuerySession session) : IQueryRunner, IContextQueryRunner
{
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

    /// <summary>Matches the cap the public delivery filters use, for the same reason.</summary>
    private const int MaxFilters = 10;

    /// <summary>What a check found: the reason to refuse, or the schema and the fields the query may name.</summary>
    private sealed record Checked(string? Refusal, ContentTypeDefinition? Schema, Dictionary<string, string> Allowed);

    private static Checked Refuse(string reason) => new(reason, null, []);

    public async Task<string?> ValidateAsync(QueryDefinition definition, CancellationToken ct) =>
        (await CheckAsync(definition, null, ct)).Refusal;

    async Task<string?> IContextQueryRunner.ValidateAsync(QueryDefinition definition, QueryRunContext context, CancellationToken ct) =>
        (await CheckAsync(definition, context, ct)).Refusal;

    private async Task<Checked> CheckAsync(QueryDefinition definition, QueryRunContext? context, CancellationToken ct)
    {
        if (definition.IsAggregate)
        {
            return Refuse("An aggregate query computes figures for a signed-in reader and cannot feed a workflow. Run it from the queries API.");
        }

        var schema = await session.Query<ContentTypeDefinition>()
            .FirstNamedAsync(definition.ContentType, ct);

        if (schema is null)
        {
            return Refuse($"Content type '{Safe(definition.ContentType)}' does not exist.");
        }

        // Public only, which is the same allowlist the anonymous delivery filters use and load
        // bearing for the same reason: filtering on a field the result cannot show is an oracle. A
        // workflow author could binary-search a Sensitive salary by watching how many rows come
        // back, without the value ever appearing in a payload.
        var allowed = schema.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public && !barakoCMS.Core.Validation.PublicFields.IsOwnerHidden(f))
            .ToDictionary(f => f.Name, f => f.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        // Null, not just empty. A request binding `filters` or `fields` to null reaches here, and
        // dereferencing it would throw out of an endpoint whose job is to answer 400 with a reason.
        if (definition.Filters is null || definition.Fields is null)
        {
            return Refuse("Filters and Fields must be present, even if Filters is empty.");
        }

        var filterRefusal = ValidateFilters(definition, schema, allowed, context);
        if (filterRefusal is not null)
        {
            return Refuse(filterRefusal);
        }

        if (!string.IsNullOrWhiteSpace(definition.SortField) && !allowed.ContainsKey(definition.SortField))
        {
            return Refuse(Unknown(definition.SortField, allowed, schema.Name, "sorted on"));
        }

        if (definition.Fields.Count == 0)
        {
            // Refused rather than defaulting to everything. "All fields" is how a personal-data
            // field added next year ends up in a payload nobody revisited.
            return Refuse("Fields must name at least one field. A query with no projection would send whatever the schema grows.");
        }

        foreach (var field in definition.Fields)
        {
            if (!allowed.ContainsKey(field))
            {
                return Refuse(Unknown(field, allowed, schema.Name, "returned from"));
            }
        }

        if (definition.Limit is < 1 or > QueryDefinition.MaxLimit)
        {
            return Refuse($"Limit must be between 1 and {QueryDefinition.MaxLimit}.");
        }

        return new Checked(null, schema, allowed);
    }

    private static string? ValidateFilters(
        QueryDefinition definition, ContentTypeDefinition schema, Dictionary<string, string> allowed, QueryRunContext? context)
    {
        if (definition.Filters!.Any(f => f is null || string.IsNullOrWhiteSpace(f.Field)))
        {
            return "Every filter needs a field.";
        }

        var lists = DeliveryQuery.ListFields(schema);

        if (definition.Filters.Count > MaxFilters)
        {
            return $"Too many filters. At most {MaxFilters} are allowed.";
        }

        foreach (var filter in definition.Filters)
        {
            if (!allowed.ContainsKey(filter.Field))
            {
                return Unknown(filter.Field, allowed, schema.Name, "filtered on");
            }

            if (!Ops.ContainsKey(filter.Op))
            {
                return $"Unknown operator '{Safe(filter.Op)}'. Supported: {string.Join(", ", Ops.Keys)}.";
            }

            if (lists.Contains(filter.Field) && Ops[filter.Op] is not (FilterOp.Eq or FilterOp.Ne))
            {
                return DeliveryQuery.ListOperatorError(filter.Op, filter.Field, offerHas: false);
            }

            if (filter.Param is not null)
            {
                if (context is null)
                {
                    return $"Filter '{Safe(filter.Field)}' takes a parameter, which only a workflow step gives a value. Use a literal value here.";
                }

                if (!barakoCMS.Core.Expressions.QueryText.IsParameter(filter.Param))
                {
                    return $"'@{Safe(filter.Param)}' is not a parameter. A parameter is @id, @createdAt, @data.Field or @actions.N.name.";
                }
            }
        }

        return null;
    }

    public async Task<QueryResult> RunAsync(QueryDefinition definition, CancellationToken ct) =>
        await RunCoreAsync(definition, null, ct);

    async Task<QueryResult> IContextQueryRunner.RunAsync(QueryDefinition definition, QueryRunContext context, CancellationToken ct) =>
        await RunCoreAsync(definition, context, ct);

    private async Task<QueryResult> RunCoreAsync(QueryDefinition definition, QueryRunContext? context, CancellationToken ct)
    {
        // Re-validated at run time, not only when it was saved. A field that was Public when
        // somebody wrote this query can be raised to Sensitive afterwards, and the query would go on
        // returning it into third-party payloads with nothing anywhere saying so.
        var check = await CheckAsync(definition, context, ct);
        if (check.Refusal is not null) return QueryResult.Refused(check.Refusal);

        var named = await session.Query<ContentTypeDefinition>()
            .Where(barakoCMS.Core.ContentTypeName.Named(definition.ContentType)).ToListAsync(ct);
        var schema = barakoCMS.Core.ContentTypeName.Pick(named, definition.ContentType)!;

        var typeOf = schema.Fields.ToDictionary(f => f.Name, f => f.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var canonical = schema.Fields.ToDictionary(f => f.Name, f => f.Name, StringComparer.OrdinalIgnoreCase);
        var lists = DeliveryQuery.ListFields(schema);

        // The schema's own spelling. Rows are stored as their creator spelled the type, so with one
        // stored type the match ignores case. With a sibling that differs only by case, the match is
        // exact, or a query for one type would return the rows of the other.
        var exactName = schema.Name;
        var typeName = exactName.ToLower();
        var query = named.Count > 1
            ? session.Query<Content>().Where(c => c.ContentType == exactName)
            : session.Query<Content>().Where(c => c.ContentType.ToLower() == typeName);

        foreach (var filter in definition.Filters)
        {
            var type = typeOf[filter.Field];
            var value = filter.Value;
            if (filter.Param is not null)
            {
                var (bound, refusal) = QueryParameters.Bind(filter.Param, type, context?.Values);
                if (refusal is not null) return QueryResult.Refused(refusal);
                value = bound!;
            }

            // The schema's own spelling, never the stored one, so what reaches the builder can only
            // be a string the content type already declared.
            var built = new DeliveryFilter(
                canonical[filter.Field], Ops[filter.Op], value, type, lists.Contains(filter.Field));

            var (sql, parameters) = DeliveryQuery.ToSql(built);
            query = query.Where(c => c.MatchesSql(sql, parameters));
        }

        if (!string.IsNullOrWhiteSpace(definition.SortField))
        {
            query = query.OrderBySql(
                DeliveryQuery.ToOrderBySql(new DeliverySort(canonical[definition.SortField], definition.Descending)));
        }

        // Validated above, so the limit is already known to be within the ceiling. There used to be
        // a Math.Clamp here as a second guard, and it was unreachable: ValidateAsync refuses an
        // over-ceiling limit before this line runs. Refusing is the better answer anyway, because
        // silently returning a thousand rows of a query that asked for a hundred thousand is a lie
        // the caller cannot see. A clamp that cannot run is not defence in depth, it is a comment
        // that looks like a guard.
        var rows = await query.Take(definition.Limit).ToListAsync(ct);

        var projected = new List<Dictionary<string, object>>(rows.Count);
        foreach (var row in rows)
        {
            var item = new Dictionary<string, object>(definition.Fields.Count, StringComparer.OrdinalIgnoreCase);

            foreach (var field in definition.Fields)
            {
                // Matched case-insensitively, the way public delivery matches, because a record can
                // hold "price" under a schema field named "Price".
                foreach (var (key, value) in row.Data)
                {
                    if (string.Equals(key, field, StringComparison.OrdinalIgnoreCase) && value is not null)
                    {
                        item[canonical[field]] = value;
                        break;
                    }
                }
            }

            projected.Add(item);
        }

        return new QueryResult(projected, null) { Ids = [.. rows.Select(r => r.Id)] };
    }

    /// <summary>A name as it may be shown back: only when it is the plain kind, so text a caller wrote is never echoed.</summary>
    private static string Safe(string? name) =>
        barakoCMS.Core.Expressions.QueryText.IsFieldName(name, allowDot: true) ? name! : "(not a valid name)";

    private static string Unknown(string field, Dictionary<string, string> allowed, string type, string verb)
    {
        var names = allowed.Count == 0 ? "(none)" : string.Join(", ", allowed.Keys.OrderBy(n => n, StringComparer.Ordinal));

        // Named rather than ignored, and the same message whether the field does not exist or is not
        // Public. Saying which would let somebody enumerate a type's Sensitive fields from here.
        return $"'{Safe(field)}' cannot be {verb} '{type}'. Fields available: {names}.";
    }
}

/// <summary>Turns the value a parameter was given into the text a filter on a field of its declared type compares.</summary>
internal static class QueryParameters
{
    /// <summary>
    /// The value of parameter <paramref name="name"/> as filter text for a field declared as
    /// <paramref name="declaredType"/>, or the reason it cannot be one.
    /// </summary>
    /// <remarks>
    /// The value is data from the entry that fired the workflow or from an earlier step, so it is read
    /// strictly. A number is a decimal in its plain form, with no group separator, currency sign or
    /// exponent, so a value of 17 digits is compared as it is written and not rounded through a
    /// double. A reference is a well-formed id, written in one spelling. The message names the
    /// parameter and never repeats the value.
    /// </remarks>
    public static (string? Text, string? Refusal) Bind(string name, string declaredType, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || !values.TryGetValue(name, out var raw) || raw is null)
        {
            return (null, $"The query needs a value for @{name}, and this run has none.");
        }

        if (raw.Length == 0)
        {
            return (null, $"The value of @{name} is empty, so it would match every entry that holds nothing in that field.");
        }

        if (barakoCMS.Core.Validation.FieldTypeRegistry.IsNumericType(declaredType))
        {
            return decimal.TryParse(
                raw.Trim(), System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? (number.ToString(System.Globalization.CultureInfo.InvariantCulture), null)
                : (null, $"The value of @{name} is not a number, and the field it is compared with holds numbers.");
        }

        if (string.Equals(declaredType, "bool", StringComparison.OrdinalIgnoreCase))
        {
            return bool.TryParse(raw.Trim(), out var flag)
                ? (flag ? "true" : "false", null)
                : (null, $"The value of @{name} is not true or false, and the field it is compared with holds one of those.");
        }

        if (string.Equals(declaredType, "reference", StringComparison.OrdinalIgnoreCase))
        {
            return Guid.TryParse(raw.Trim(), out var id)
                ? (id.ToString(), null)
                : (null, $"The value of @{name} is not an id, and the field it is compared with holds a reference.");
        }

        return (raw, null);
    }
}
