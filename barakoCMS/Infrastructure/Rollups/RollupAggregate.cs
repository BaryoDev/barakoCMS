using System.Globalization;
using System.Text.Json.Serialization;
using barakoCMS.Core.Validation;
using barakoCMS.Features.Queries.Run;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Rollups;

/// <summary>
/// The value of a rollup field for a set of header entries, read from the entries that point at them.
/// The mark drain and the backfill both call it, so a header gets one answer either way.
/// </summary>
/// <remarks>
/// One query per rollup field. Every value in it is a bound parameter: the type, the field names,
/// the header ids and the status and sensitivity lists. The text changes only in how many
/// placeholders a list needs. It scans the tenant's rows of the <c>from</c> type, which is why a
/// large type is better served once an index exists.
/// </remarks>
internal static class RollupAggregate
{
    private sealed class Row
    {
        [JsonPropertyName("Header")]
        public string? Header { get; set; }

        [JsonPropertyName("Value")]
        public string? Value { get; set; }
    }

    /// <summary>The statuses an entry may have to count toward a rollup at this level.</summary>
    internal static ContentStatus[] CountedStatuses(SensitivityLevel level) =>
        SensitivityRank.Exposure(level) >= SensitivityRank.Exposure(SensitivityLevel.Members)
            ? [ContentStatus.Published]
            : [ContentStatus.Draft, ContentStatus.Published, ContentStatus.Scheduled];

    /// <summary>The own levels an entry may have to count: not Hidden, and no stricter than the rollup.</summary>
    internal static SensitivityLevel[] CountedLevels(SensitivityLevel level) =>
        Enum.GetValues<SensitivityLevel>()
            .Where(l => l != SensitivityLevel.Hidden && SensitivityRank.Exposure(l) >= SensitivityRank.Exposure(level))
            .ToArray();

    /// <summary>
    /// The value of <paramref name="rollup"/> for each header id. A header that nothing points at
    /// gets the empty value (0 for count and sum, null otherwise). A header whose value cannot be
    /// held, a sum past the range of a decimal, is left out of the result, so its stored value stays.
    /// </summary>
    /// <param name="source">The summed field on the <c>from</c> type, or null for a count.</param>
    public static async Task<Dictionary<Guid, object?>> ValuesAsync(
        IDocumentSession session, FieldDefinition rollup, FieldDefinition? source, IReadOnlyCollection<Guid> headers, CancellationToken ct,
        SensitivityLevel? level = null)
    {
        var values = new Dictionary<Guid, object?>();
        if (headers.Count == 0)
        {
            return values;
        }

        var def = rollup.Rollup!;
        var fn = RollupFields.FnOf(def);
        var kind = fn == "count" ? "int" : RollupFields.Canon(source?.Type);
        var empty = fn is "count" or "sum" ? Typed(0m, rollup) : null;
        foreach (var id in headers)
        {
            values[id] = empty;
        }

        var (sql, args) = Build(session, rollup, fn, kind, headers, level ?? rollup.Sensitivity);
        var rows = await session.QueryAsync<Row>(sql, ct, args);
        foreach (var row in rows)
        {
            if (!Guid.TryParse(row.Header, out var id) || !values.ContainsKey(id))
            {
                continue;
            }

            if (!Parse(row.Value, fn, kind, rollup, out var value))
            {
                values.Remove(id);
                continue;
            }

            values[id] = value;
        }

        return values;
    }

    private static (string Sql, object[] Args) Build(
        IDocumentSession session, FieldDefinition rollup, string fn, string kind, IReadOnlyCollection<Guid> headers, SensitivityLevel level)
    {
        var def = rollup.Rollup!;
        var args = new List<object>();
        var schema = session.DocumentStore.Options.DatabaseSchemaName;

        // The parameters are added in the order their placeholders appear in the text: the select
        // list, then the inner select, then its where clause, then the outer one.
        var value = ValueSql(fn, kind, def, args);
        args.Add(def.Via);
        var summed = string.Empty;
        if (fn != "count")
        {
            args.Add(def.Field!);
            summed = ", " + (kind is "date" or "datetime"
                ? EntryFieldSql.DateValue("lower(?)", "d", "'UTC'")
                : EntryFieldSql.Number("lower(?)", "d")) + " as n";
        }

        args.Add(session.TenantId);
        args.Add(def.From.ToLowerInvariant());
        var statuses = CountedStatuses(level);
        var levels = CountedLevels(level);
        args.AddRange(statuses.Select(s => (object)(int)s));
        args.AddRange(levels.Select(l => (object)(int)l));
        args.AddRange(headers.Select(h => (object)h.ToString()));

        var sql = $"select jsonb_build_object('Header', t.ref, 'Value', {value}) from ("
            + $"select unnest({EntryFieldSql.References("lower(?)", "d")}) as ref{summed} "
            + $"from {schema}.mt_doc_contents d "
            + "where d.tenant_id = ? and lower(d.data ->> 'ContentType') = ? "
            + $"and (d.data ->> 'Status')::int in ({Marks(statuses.Length)}) "
            + $"and (d.data ->> 'Sensitivity')::int in ({Marks(levels.Length)})) t "
            + $"where t.ref in ({Marks(headers.Count)}) group by t.ref";
        return (sql, args.ToArray());
    }

    private static string Marks(int count) => string.Join(", ", Enumerable.Repeat("?", count));

    /// <summary>The aggregate as text. Its parameter, when it has one, is added to <paramref name="args"/>.</summary>
    private static string ValueSql(string fn, string kind, RollupDefinition def, List<object> args)
    {
        switch (fn)
        {
            case "count":
                return "count(*)::text";
            case "sum":
                if (def.Round is { } places)
                {
                    args.Add(places);
                    return "round(coalesce(sum(t.n), 0), ?)::text";
                }

                return "coalesce(sum(t.n), 0)::text";
            case "avg":
                args.Add(def.Round ?? 2);
                return "round(avg(t.n), ?)::text";
        }

        if (kind == "date")
        {
            return $"to_char({fn}(t.n) at time zone 'UTC', 'YYYY-MM-DD')";
        }

        if (kind == "datetime")
        {
            return $"to_char({fn}(t.n) at time zone 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"')";
        }

        return $"{fn}(t.n)::text";
    }

    /// <summary>The text the query gave as the value the field holds. False when it cannot be held.</summary>
    private static bool Parse(string? text, string fn, string kind, FieldDefinition rollup, out object? value)
    {
        value = null;
        if (text is null)
        {
            return true;
        }

        if (fn != "count" && kind is "date" or "datetime")
        {
            value = text;
            return true;
        }

        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        value = Typed(number, rollup, fn == "min" || fn == "max" ? kind : null);
        return true;
    }

    /// <summary>
    /// The number as the field stores it: a whole number for an int field, and otherwise a decimal
    /// without trailing zeros, so the same total always compares equal to what was stored.
    /// </summary>
    private static object Typed(decimal number, FieldDefinition rollup, string? kind = null)
    {
        var declared = kind ?? RollupFields.Canon(rollup.Type);
        if (declared == "int")
        {
            return (long)Math.Truncate(number);
        }

        return number / 1.000000000000000000000000000000000m;
    }
}
