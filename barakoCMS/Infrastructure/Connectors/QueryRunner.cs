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

    public static QueryResult Refused(string reason) => new([], reason);
}

public interface IQueryRunner
{
    /// <summary>Runs a saved query and returns only the fields it names.</summary>
    Task<QueryResult> RunAsync(QueryDefinition definition, CancellationToken ct);

    /// <summary>Why this definition may not be saved or run, or null.</summary>
    Task<string?> ValidateAsync(QueryDefinition definition, CancellationToken ct);
}

internal sealed class QueryRunner : IQueryRunner
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

    private readonly IQuerySession _session;

    public QueryRunner(IQuerySession session) => _session = session;

    public async Task<string?> ValidateAsync(QueryDefinition definition, CancellationToken ct)
    {
        var schema = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == definition.ContentType, ct);

        if (schema is null)
        {
            return $"Content type '{definition.ContentType}' does not exist.";
        }

        // Public only, which is the same allowlist the anonymous delivery filters use and load
        // bearing for the same reason: filtering on a field the result cannot show is an oracle. A
        // workflow author could binary-search a Sensitive salary by watching how many rows come
        // back, without the value ever appearing in a payload.
        var allowed = schema.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public)
            .ToDictionary(f => f.Name, f => f.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase);

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
                return $"Unknown operator '{filter.Op}'. Supported: {string.Join(", ", Ops.Keys)}.";
            }
        }

        if (!string.IsNullOrWhiteSpace(definition.SortField) && !allowed.ContainsKey(definition.SortField))
        {
            return Unknown(definition.SortField, allowed, schema.Name, "sorted on");
        }

        if (definition.Fields.Count == 0)
        {
            // Refused rather than defaulting to everything. "All fields" is how a personal-data
            // field added next year ends up in a payload nobody revisited.
            return "Fields must name at least one field. A query with no projection would send whatever the schema grows.";
        }

        foreach (var field in definition.Fields)
        {
            if (!allowed.ContainsKey(field))
            {
                return Unknown(field, allowed, schema.Name, "returned from");
            }
        }

        if (definition.Limit is < 1 or > QueryDefinition.MaxLimit)
        {
            return $"Limit must be between 1 and {QueryDefinition.MaxLimit}.";
        }

        return null;
    }

    public async Task<QueryResult> RunAsync(QueryDefinition definition, CancellationToken ct)
    {
        // Re-validated at run time, not only when it was saved. A field that was Public when
        // somebody wrote this query can be raised to Sensitive afterwards, and the query would go on
        // returning it into third-party payloads with nothing anywhere saying so.
        var refusal = await ValidateAsync(definition, ct);
        if (refusal is not null) return QueryResult.Refused(refusal);

        var schema = (await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == definition.ContentType, ct))!;

        var typeOf = schema.Fields.ToDictionary(f => f.Name, f => f.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var canonical = schema.Fields.ToDictionary(f => f.Name, f => f.Name, StringComparer.OrdinalIgnoreCase);

        var query = _session.Query<Content>()
            .Where(c => c.ContentType == definition.ContentType);

        foreach (var filter in definition.Filters)
        {
            // The schema's own spelling, never the stored one, so what reaches the builder can only
            // be a string the content type already declared.
            var built = new DeliveryFilter(
                canonical[filter.Field], Ops[filter.Op], filter.Value, typeOf[filter.Field]);

            var (sql, parameters) = DeliveryQuery.ToSql(built);
            query = query.Where(c => c.MatchesSql(sql, parameters));
        }

        if (!string.IsNullOrWhiteSpace(definition.SortField))
        {
            query = query.OrderBySql(
                DeliveryQuery.ToOrderBySql(new DeliverySort(canonical[definition.SortField], definition.Descending)));
        }

        // Capped here as well as validated. The ceiling is the thing standing between a
        // misconfiguration and an email to everyone, so it does not rely on the save path having run.
        var take = Math.Clamp(definition.Limit, 1, QueryDefinition.MaxLimit);

        var rows = await query.Take(take).ToListAsync(ct);

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

        return new QueryResult(projected, null);
    }

    private static string Unknown(string field, Dictionary<string, string> allowed, string type, string verb)
    {
        var names = allowed.Count == 0 ? "(none)" : string.Join(", ", allowed.Keys.OrderBy(n => n, StringComparer.Ordinal));

        // Named rather than ignored, and the same message whether the field does not exist or is not
        // Public. Saying which would let somebody enumerate a type's Sensitive fields from here.
        return $"'{field}' cannot be {verb} '{type}'. Fields available: {names}.";
    }
}
