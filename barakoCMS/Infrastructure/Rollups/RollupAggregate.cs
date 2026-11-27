using System.Globalization;
using System.Text.Json.Serialization;
using barakoCMS.Core.Validation;
using barakoCMS.Features.Queries.Run;
using barakoCMS.Infrastructure.Logging;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

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
    /// <summary>
    /// Where a skipped header is reported when the caller passes no logger. The drain and the
    /// field change have none to hand, and a skip must not go unsaid.
    /// </summary>
    private static readonly ILogger Fallback = new SerilogLoggerFactory().CreateLogger(typeof(RollupAggregate).FullName!);

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
    /// held, a sum past the range of a decimal or of a whole-number field, is left out of the
    /// result, so its stored value stays, and one warning names the type, the field and the header.
    /// </summary>
    /// <param name="headerType">The type that declares the rollup, for the warning.</param>
    /// <param name="source">The summed field on the <c>from</c> type, or null for a count.</param>
    /// <param name="clock">What a window counts by. Null is the system clock, and only a windowed rollup reads it.</param>
    public static async Task<Dictionary<Guid, object?>> ValuesAsync(
        IDocumentSession session, string headerType, FieldDefinition rollup, FieldDefinition? source,
        IReadOnlyCollection<Guid> headers, CancellationToken ct, SensitivityLevel? level = null, ILogger? log = null,
        RollupClock? clock = null)
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

        var today = RollupFields.WindowOf(def) is null ? null : await (clock ?? new RollupClock()).TodayAsync(session, ct);
        var (sql, args) = Build(session, rollup, fn, kind, headers, level ?? rollup.Sensitivity, today);
        var rows = await session.QueryAsync<Row>(sql, ct, args);
        foreach (var row in rows)
        {
            if (!Guid.TryParse(row.Header, out var id) || !values.ContainsKey(id))
            {
                continue;
            }

            if (!Parse(row.Value, fn, kind, rollup, out var value))
            {
                // No value in the log: only where to look.
                (log ?? Fallback).LogWarning(
                    "Rollup {Type}.{Field} skipped header {Header}: its value does not fit the field, so the stored value stays",
                    LogSafe.Value(headerType), LogSafe.Value(rollup.Name), id);
                values.Remove(id);
                continue;
            }

            values[id] = value;
        }

        return values;
    }

    private static (string Sql, object[] Args) Build(
        IDocumentSession session, FieldDefinition rollup, string fn, string kind, IReadOnlyCollection<Guid> headers, SensitivityLevel level,
        RollupToday? today)
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

        // A window on a date or datetime field reads the value as a column of the inner select and
        // filters on it outside. A window on createdAt filters inside, on the indexed expression.
        var window = today is null ? null : RollupFields.WindowOf(def);
        var onField = window is { } w && !RollupFields.IsCreatedAt(w.Key);
        var fromWindow = onField ? FieldWindowSelect(window!.Value.Key, today!.Zone, args) : string.Empty;

        args.Add(session.TenantId);
        args.Add(def.From.ToLowerInvariant());
        var statuses = CountedStatuses(level);
        var levels = CountedLevels(level);
        args.AddRange(statuses.Select(s => (object)(int)s));
        args.AddRange(levels.Select(l => (object)(int)l));

        var created = string.Empty;
        var local = string.Empty;
        if (window is { } chosen)
        {
            var days = RelativeDateWindow.Resolve(chosen.Word, today!.Date);
            created = onField ? string.Empty : CreatedFilter(schema, days, today.Zone, args);
            local = onField ? LocalFilter(days, today.Zone, args) : string.Empty;
        }

        args.AddRange(headers.Select(h => (object)h.ToString()));

        var sql = $"select jsonb_build_object('Header', t.ref, 'Value', {value}) from ("
            + $"select unnest({EntryFieldSql.References("lower(?)", "d")}) as ref{summed}{fromWindow} "
            + $"from {schema}.mt_doc_contents d "
            + "where d.tenant_id = ? and lower(d.data ->> 'ContentType') = ? "
            + $"and (d.data ->> 'Status')::int in ({Marks(statuses.Length)}) "
            + $"and (d.data ->> 'Sensitivity')::int in ({Marks(levels.Length)}){created}) t "
            + $"where {local}t.ref in ({Marks(headers.Count)}) group by t.ref";
        return (sql, args.ToArray());
    }

    /// <summary>
    /// The window field as a column of the inner select. The value expression names the zone twice
    /// before the lookup names the key, so the zone is bound twice and then the key.
    /// </summary>
    private static string FieldWindowSelect(string key, TimeZoneInfo zone, List<object> args)
    {
        args.Add(zone.Id);
        args.Add(zone.Id);
        args.Add(key);
        return ", " + EntryFieldSql.DateValue("lower(?)", "d", "?::text") + " as w";
    }

    /// <summary>The createdAt window, on the indexed expression. Bounds are unspecified wall times so they bind as timestamp.</summary>
    private static string CreatedFilter(string schema, (DateOnly From, DateOnly To) days, TimeZoneInfo zone, List<object> args)
    {
        var (fromUtc, toUtc) = RelativeDateWindow.UtcBounds(days.From, days.To, zone);
        args.Add(fromUtc);
        args.Add(toUtc);
        var stamp = $"{schema}.mt_immutable_timestamp(d.data ->> 'CreatedAt')";
        return $" and {stamp} >= ?::timestamp and {stamp} < ?::timestamp";
    }

    /// <summary>The window on a date or datetime field, compared as the local date in the site zone.</summary>
    private static string LocalFilter((DateOnly From, DateOnly To) days, TimeZoneInfo zone, List<object> args)
    {
        args.Add(zone.Id);
        args.Add(days.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        args.Add(zone.Id);
        args.Add(days.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return "(t.w at time zone ?::text)::date >= ?::date and (t.w at time zone ?::text)::date <= ?::date and ";
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

        var declared = fn is "min" or "max" ? kind : RollupFields.Canon(rollup.Type);
        if (declared == "int" && (Math.Truncate(number) < long.MinValue || Math.Truncate(number) > long.MaxValue))
        {
            return false;
        }

        value = Typed(number, rollup, fn is "min" or "max" ? kind : null);
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
