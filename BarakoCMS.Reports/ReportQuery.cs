using System.Globalization;
using System.Text;
using System.Text.Json;
using barakoCMS.Core.Validation;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Reports;

/// <summary>The groups and totals a run computed.</summary>
internal sealed class ReportResult
{
    public List<ReportRow> Rows { get; set; } = [];
    public ReportRow Totals { get; set; } = new();

    /// <summary>True when more groups matched than the limit allowed.</summary>
    public bool Truncated { get; set; }
}

internal sealed class ReportRow
{
    public string? Key { get; set; }
    public long Count { get; set; }
    public Dictionary<string, decimal?> Measures { get; set; } = [];
}

/// <summary>What the database returns for one group, before the labels are put back.</summary>
internal sealed class GroupRow
{
    public string? Key { get; set; }
    public long Count { get; set; }
    public Dictionary<string, decimal?> M { get; set; } = [];
}

/// <summary>A report ready to run: every field resolved to the schema's spelling and checked readable.</summary>
internal sealed class ReportPlan
{
    public required string ContentType { get; init; }
    public string? GroupField { get; init; }
    public required IReadOnlyList<(ReportMeasure Measure, string? Field)> Measures { get; init; }
    public required IReadOnlyList<(ReportFilter Filter, FieldDefinition Field)> Filters { get; init; }
    public string? DateField { get; init; }
    public int GroupLimit { get; init; }

    /// <summary>The read rules of the caller, as a predicate over the alias <c>d</c>.</summary>
    public required string AccessSql { get; init; }
    public required object[] AccessParameters { get; init; }

    /// <summary>Sensitivity levels (as stored, number and name) the caller may not see.</summary>
    public required string[] WithheldSensitivity { get; init; }
}

/// <summary>
/// Builds and runs the one grouped query. Every field name and value reaches the database as a
/// bound parameter, and the SQL text holds only fragments written here.
/// </summary>
internal static class ReportQuery
{
    private static readonly Dictionary<string, string> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eq"] = "=", ["ne"] = "<>", ["lt"] = "<", ["lte"] = "<=", ["gt"] = ">", ["gte"] = ">=",
    };

    public static readonly string[] Functions = ["count", "sum", "avg", "min", "max"];

    public static bool IsOperator(string op) => Operators.ContainsKey(op);

    /// <summary>
    /// Finds a field's value by key, case-insensitively, the way delivery does, so a report and a
    /// list agree on which entries hold a value.
    /// </summary>
    private const string KeyLookup =
        "(SELECT e.value FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower(?) LIMIT 1)";

    private static string NumberOf(string lookup) =>
        $"CASE WHEN jsonb_typeof({lookup}) = 'number' THEN ({lookup} #>> '{{}}')::numeric END";

    public static async Task<ReportResult> RunAsync(
        IQuerySession session, ReportPlan plan, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var schema = session.DocumentStore.Options.DatabaseSchemaName;
        var measured = plan.Measures.Where(m => m.Measure.Fn != "count").ToList();

        var (where, whereParameters) = Where(plan, session.TenantId, from, to);

        // Select list first: its parameters come before the WHERE clause's, in the text.
        var inner = new StringBuilder();
        var innerParameters = new List<object>();
        inner.Append(plan.GroupField is null
            ? "null::text AS grp"
            : $"{KeyLookup} #>> '{{}}' AS grp");
        if (plan.GroupField is not null) innerParameters.Add(plan.GroupField);
        for (var i = 0; i < measured.Count; i++)
        {
            inner.Append($", {NumberOf(KeyLookup)} AS m{i}");
            innerParameters.Add(measured[i].Field!);
            innerParameters.Add(measured[i].Field!);
        }

        var aggregates = new StringBuilder();
        for (var i = 0; i < measured.Count; i++)
        {
            if (i > 0) aggregates.Append(", ");
            var fn = measured[i].Measure.Fn;
            var call = fn == "avg" ? $"round(avg(t.m{i}), 6)" : $"{fn}(t.m{i})";
            aggregates.Append($"'m{i}', {call}");
        }

        var from_ = $"FROM {schema}.mt_doc_contents d WHERE {where}";
        var parameters = innerParameters.Concat(whereParameters).ToArray();

        var measuresJson = measured.Count == 0 ? "'{}'::jsonb" : $"jsonb_build_object({aggregates})";

        // One more than the limit, so the run can say it was cut short.
        var grouped = await session.QueryAsync<GroupRow>(
            $"SELECT jsonb_build_object('Key', t.grp, 'Count', count(*), 'M', {measuresJson}) "
            + $"FROM (SELECT {inner} {from_}) t GROUP BY t.grp ORDER BY t.grp ASC NULLS LAST LIMIT {plan.GroupLimit + 1}",
            ct, parameters);

        var totals = (await session.QueryAsync<GroupRow>(
            $"SELECT jsonb_build_object('Key', null, 'Count', count(*), 'M', {measuresJson}) "
            + $"FROM (SELECT {inner} {from_}) t",
            ct, parameters)).Single();

        ReportRow Label(GroupRow g) => new()
        {
            Key = g.Key,
            Count = g.Count,
            Measures = plan.Measures.ToDictionary(
                m => m.Measure.Label,
                m => m.Measure.Fn == "count"
                    ? (decimal?)g.Count
                    : g.M.GetValueOrDefault($"m{measured.FindIndex(x => ReferenceEquals(x.Measure, m.Measure))}")),
        };

        return new ReportResult
        {
            Rows = grouped.Take(plan.GroupLimit).Select(Label).ToList(),
            Truncated = grouped.Count > plan.GroupLimit,
            Totals = Label(totals),
        };
    }

    private static (string Sql, List<object> Parameters) Where(ReportPlan plan, string tenant, DateOnly? from, DateOnly? to)
    {
        var parts = new List<string> { "d.tenant_id = ?", "lower(d.data ->> 'ContentType') = lower(?)" };
        var parameters = new List<object> { tenant, plan.ContentType };

        parts.Add($"({plan.AccessSql})");
        parameters.AddRange(plan.AccessParameters);

        if (plan.WithheldSensitivity.Length > 0)
        {
            parts.Add("coalesce(d.data ->> 'Sensitivity', '0') <> ALL(?)");
            parameters.Add(plan.WithheldSensitivity);
        }

        foreach (var (filter, field) in plan.Filters)
        {
            parts.Add($"{KeyLookup} {Operators[filter.Op]} ?::jsonb");
            parameters.Add(field.Name);
            parameters.Add(JsonLiteral(filter.Value, field.Type));
        }

        // Text comparison on an ISO date, which sorts the way the dates do. The upper bound is the
        // day after, so the day named is included whatever time of day the entry holds.
        var date = plan.DateField is null
            ? "(d.data ->> 'CreatedAt')"
            : $"({KeyLookup} #>> '{{}}')";
        if (from is { } start)
        {
            parts.Add($"{date} >= ?");
            if (plan.DateField is not null) parameters.Add(plan.DateField);
            parameters.Add(start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (to is { } end)
        {
            parts.Add($"{date} < ?");
            if (plan.DateField is not null) parameters.Add(plan.DateField);
            parameters.Add(end.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return (string.Join(" AND ", parts), parameters);
    }

    /// <summary>The value as a JSON scalar: a number bare when the field is numeric, quoted otherwise.</summary>
    private static string JsonLiteral(string value, string declaredType)
    {
        if (FieldTypeRegistry.IsNumericType(declaredType)
            && decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
            return n.ToString(CultureInfo.InvariantCulture);

        if (string.Equals(declaredType, "bool", StringComparison.OrdinalIgnoreCase)
            && bool.TryParse(value, out var b))
            return b ? "true" : "false";

        return JsonSerializer.Serialize(value);
    }
}
