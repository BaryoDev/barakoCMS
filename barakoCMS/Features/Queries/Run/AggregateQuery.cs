using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Features.Public;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;
using Npgsql;
using NpgsqlTypes;
using Entry = barakoCMS.Models.Content;

namespace barakoCMS.Features.Queries.Run;

internal sealed record AggregateResult(
    IReadOnlyList<Dictionary<string, object?>> Rows,
    Dictionary<string, object?> Totals,
    string? Refusal)
{
    public bool Ok => Refusal is null;

    public static AggregateResult Refused(string reason) => new([], new Dictionary<string, object?>(), reason);
}

/// <summary>
/// Plans and runs a saved aggregate query: group, count and sum in the database, under the rules
/// of the person asking.
/// </summary>
/// <remarks>
/// The caller decides three things. A field they may not read cannot be grouped, measured, filtered
/// or ranged on, because the figure it produces is the value read back out. A row their Read rules
/// hide is not in the set. A document sensitivity they may not see is not in the set. Marten still
/// writes the tenant filter, the type filter and the field filters; the grouping runs over that
/// command as a subquery, so none of those can be skipped by the aggregate text.
///
/// Every name that reaches SQL text is a field the schema declares, checked against a strict
/// pattern. Every value is a parameter.
/// </remarks>
internal sealed partial class AggregateQuery(
    IQuerySession session,
    IPermissionResolver permissions,
    ISensitivityService sensitivity)
{
    public const int MaxGroups = 1000;
    private const int MaxFilters = 10;
    private const int MaxMeasures = 20;
    private const int MaxGroupBy = 3;
    private const int MaxFilterValueLength = 256;

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

    private static readonly string[] Functions = ["sum", "count", "avg", "min", "max"];
    private static readonly string[] Buckets = ["day", "month", "year"];
    private static readonly string[] Relatives = ["today", "last7Days", "last30Days", "thisMonth", "lastMonth", "thisYear"];

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\$(\d+)")]
    private static partial Regex PositionalParameter();

    public async Task<AggregateResult> RunAsync(
        QueryDefinition definition, ClaimsPrincipal caller, string? from, string? to, bool execute, CancellationToken ct)
    {
        if (!Guid.TryParse(caller.FindFirst("UserId")?.Value, out var userId)
            || await session.LoadAsync<User>(userId, ct) is not { } user)
        {
            return AggregateResult.Refused("Sign in to run this query.");
        }

        var plan = await PlanAsync(definition, user, caller, from, to, ct);
        if (plan.Refusal is not null) return AggregateResult.Refused(plan.Refusal);
        if (!execute) return new AggregateResult([], new Dictionary<string, object?>(), null);

        try
        {
            return await ExecuteAsync(plan, ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidParameterValue)
        {
            return AggregateResult.Refused($"The site's time zone '{plan.ZoneId}' is not one the database knows, so days cannot be told apart.");
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.DatetimeFieldOverflow
                                          or PostgresErrorCodes.InvalidTimeZoneDisplacementValue
                                          or PostgresErrorCodes.InvalidDatetimeFormat
                                          or PostgresErrorCodes.NumericValueOutOfRange)
        {
            return AggregateResult.Refused("A stored value is outside the range the database can compute with, so this figure cannot be taken.");
        }
        catch (OverflowException)
        {
            return AggregateResult.Refused("A total is too large to report.");
        }
    }

    // ---- planning ---------------------------------------------------------------------------

    private sealed record Scope(
        ContentTypeDefinition Definition, Dictionary<string, FieldDefinition> Readable, ReadPredicate Predicate);

    private sealed record GroupPlan(string Field, string? Bucket, string Type);

    private sealed record MeasurePlan(string Fn, string? Field, string Alias, string? Via);

    private sealed class Plan
    {
        public string? Refusal;
        public string ContentType = string.Empty;
        public IQueryable<Entry> Header = null!;
        public readonly List<GroupPlan> Groups = [];
        public readonly List<MeasurePlan> Measures = [];
        public readonly Dictionary<string, IQueryable<Entry>> Targets = new(StringComparer.OrdinalIgnoreCase);
        public string? RangeField;
        public DateTime? RangeFrom;
        public DateTime? RangeTo;
        public string ZoneId = "UTC";
    }

    private async Task<Plan> PlanAsync(
        QueryDefinition definition, User user, ClaimsPrincipal caller, string? from, string? to, CancellationToken ct)
    {
        var plan = new Plan { ContentType = definition.ContentType };

        if (definition.Measures is not { Count: > 0 } measures)
            return Fail(plan, "An aggregate query needs at least one measure.");
        if (measures.Count > MaxMeasures)
            return Fail(plan, $"Too many measures. At most {MaxMeasures} are allowed.");

        var groupBy = definition.GroupBy ?? [];
        if (groupBy.Count > MaxGroupBy)
            return Fail(plan, $"Too many group fields. At most {MaxGroupBy} are allowed.");

        var filters = definition.Filters ?? [];
        if (filters.Count > MaxFilters)
            return Fail(plan, $"Too many filters. At most {MaxFilters} are allowed.");

        var (scope, scopeRefusal) = await ScopeAsync(definition.ContentType, user, caller, ct);
        if (scope is null) return Fail(plan, scopeRefusal!);

        var query = await ScopedAsync(definition.ContentType, scope.Predicate, caller, ct);

        var lists = DeliveryQuery.ListFields(scope.Definition);
        foreach (var filter in filters)
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.Field))
                return Fail(plan, "Every filter needs a field.");
            if (!scope.Readable.TryGetValue(filter.Field, out var declared))
                return Fail(plan, Unreadable(filter.Field, definition.ContentType));
            if (!Ops.TryGetValue(filter.Op ?? string.Empty, out var op))
                return Fail(plan, $"Unknown operator '{filter.Op}'. Supported: {string.Join(", ", Ops.Keys)}.");
            if ((filter.Value?.Length ?? 0) > MaxFilterValueLength)
                return Fail(plan, $"The value of the filter on '{declared.Name}' is too long.");

            var multiple = lists.Contains(declared.Name);
            if (multiple && op is not (FilterOp.Eq or FilterOp.Ne))
                return Fail(plan, DeliveryQuery.ListOperatorError(filter.Op!, declared.Name, offerHas: false));

            var (sql, parameters) = DeliveryQuery.ToSql(
                new DeliveryFilter(declared.Name, op, filter.Value ?? string.Empty, declared.Type ?? string.Empty, multiple));
            query = query.Where(c => c.MatchesSql(sql, parameters));
        }

        plan.Header = query;

        var seenGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groupBy)
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Field))
                return Fail(plan, "Every group needs a field.");
            if (!scope.Readable.TryGetValue(group.Field, out var declared))
                return Fail(plan, Unreadable(group.Field, definition.ContentType));
            if (!UsableName(declared.Name))
                return Fail(plan, $"'{declared.Name}' cannot be grouped on: its name has characters a figure cannot be built from.");
            if (lists.Contains(declared.Name))
                return Fail(plan, $"'{declared.Name}' holds a list and cannot be grouped on.");
            if (!seenGroups.Add(declared.Name))
                return Fail(plan, $"'{declared.Name}' is grouped on twice.");

            var bucket = string.IsNullOrWhiteSpace(group.Bucket) ? null : group.Bucket.Trim().ToLowerInvariant();
            if (bucket is not null)
            {
                if (!Buckets.Contains(bucket))
                    return Fail(plan, $"Unknown bucket '{group.Bucket}'. Supported: {string.Join(", ", Buckets)}.");
                if (!IsDate(declared.Type))
                    return Fail(plan, $"'{declared.Name}' is not a date field, so it cannot be bucketed by {bucket}.");
            }

            plan.Groups.Add(new GroupPlan(declared.Name, bucket, declared.Type ?? string.Empty));
        }

        var aliases = new HashSet<string>(seenGroups, StringComparer.OrdinalIgnoreCase);
        foreach (var measure in measures)
        {
            var refusal = await PlanMeasureAsync(plan, scope, definition.ContentType, measure, aliases, user, caller, ct);
            if (refusal is not null) return Fail(plan, refusal);
        }

        var needsZone = plan.Groups.Any(g => g.Bucket is not null) || definition.Range is not null;
        if (needsZone)
        {
            var (zone, zoneRefusal) = await ZoneAsync(ct);
            if (zone is null) return Fail(plan, zoneRefusal!);
            plan.ZoneId = zone.Id;

            if (definition.Range is { } range)
            {
                var rangeRefusal = PlanRange(plan, scope, definition.ContentType, range, from, to, zone);
                if (rangeRefusal is not null) return Fail(plan, rangeRefusal);
            }
        }

        return plan;
    }

    private async Task<string?> PlanMeasureAsync(
        Plan plan, Scope scope, string contentType, QueryMeasure measure, HashSet<string> aliases,
        User user, ClaimsPrincipal caller, CancellationToken ct)
    {
        if (measure is null) return "Every measure needs a function.";

        var fn = measure.Fn?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!Functions.Contains(fn))
            return $"Unknown function '{measure.Fn}'. Supported: {string.Join(", ", Functions)}.";

        var alias = measure.As?.Trim() ?? string.Empty;
        if (!NamePattern().IsMatch(alias))
            return "Every measure needs a name of letters, digits and underscores, starting with a letter.";
        if (!aliases.Add(alias))
            return $"The name '{alias}' is used twice.";

        var fieldScope = scope;
        var typeName = contentType;
        string? via = null;

        if (!string.IsNullOrWhiteSpace(measure.Via))
        {
            if (measure.Via.Contains('.'))
                return $"'{measure.Via}' goes more than one hop. A measure follows one reference, no further.";
            if (!scope.Readable.TryGetValue(measure.Via, out var link))
                return Unreadable(measure.Via, contentType);
            if (!string.Equals(link.Type, "reference", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(link.ReferenceType) || !UsableName(link.Name))
                return $"'{measure.Via}' is not a reference field.";

            var (target, targetRefusal) = await ScopeAsync(link.ReferenceType, user, caller, ct);
            if (target is null) return targetRefusal;

            via = link.Name;
            fieldScope = target;
            typeName = link.ReferenceType;

            if (!plan.Targets.ContainsKey(via))
                plan.Targets[via] = await ScopedAsync(link.ReferenceType, target.Predicate, caller, ct);
        }

        string? field = null;
        if (!string.IsNullOrWhiteSpace(measure.Field))
        {
            if (!fieldScope.Readable.TryGetValue(measure.Field, out var declared))
                return Unreadable(measure.Field, typeName);
            if (!UsableName(declared.Name))
                return $"'{declared.Name}' cannot be measured: its name has characters a figure cannot be built from.";
            if (fn != "count" && !FieldTypeRegistry.IsNumericType(declared.Type))
                return $"'{declared.Name}' is not a number, so it cannot be used with {fn}.";
            field = declared.Name;
        }
        else if (fn != "count")
        {
            return $"{fn} needs a field.";
        }

        plan.Measures.Add(new MeasurePlan(fn, field, alias, via));
        return null;
    }

    private static string? PlanRange(
        Plan plan, Scope scope, string contentType, QueryRange range, string? from, string? to, TimeZoneInfo zone)
    {
        if (!scope.Readable.TryGetValue(range.Field ?? string.Empty, out var declared))
            return Unreadable(range.Field ?? string.Empty, contentType);
        if (!IsDate(declared.Type) || !UsableName(declared.Name))
            return $"'{declared.Name}' is not a date field, so it cannot hold a range.";

        plan.RangeField = declared.Name;

        var hasRelative = !string.IsNullOrWhiteSpace(range.Relative);
        if (hasRelative && (!string.IsNullOrWhiteSpace(range.From) || !string.IsNullOrWhiteSpace(range.To)))
            return "A range is either relative or has from and to, not both.";
        if (hasRelative && !Relatives.Contains(range.Relative!, StringComparer.Ordinal))
            return $"Unknown range '{range.Relative}'. Supported: {string.Join(", ", Relatives)}.";

        DateOnly? start = null;
        DateOnly? end = null;
        if (!TryDay(range.From, out start) || !TryDay(range.To, out end))
            return "A range date must be written yyyy-MM-dd.";
        if (!TryDay(from, out var runFrom) || !TryDay(to, out var runTo))
            return "from and to must be written yyyy-MM-dd.";

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        DateOnly? windowStart = runFrom ?? start;
        DateOnly? windowEnd = runTo ?? end;

        if (hasRelative && runFrom is null && runTo is null)
        {
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            (windowStart, windowEnd) = range.Relative switch
            {
                "today" => (today, today),
                "last7Days" => (today.AddDays(-6), today),
                "last30Days" => (today.AddDays(-29), today),
                "thisMonth" => (monthStart, monthStart.AddMonths(1).AddDays(-1)),
                "lastMonth" => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
                _ => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
            };
        }

        if (windowStart is { } a && windowEnd is { } b && a > b)
            return "The range starts after it ends.";

        plan.RangeFrom = windowStart is { } s ? ToUtc(s.ToDateTime(TimeOnly.MinValue), zone) : null;
        plan.RangeTo = windowEnd is { } e ? ToUtc(e.AddDays(1).ToDateTime(TimeOnly.MinValue), zone) : null;
        return null;
    }

    private static bool TryDay(string? text, out DateOnly? day)
    {
        day = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        day = parsed;
        return true;
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
    }

    private async Task<(TimeZoneInfo? Zone, string? Refusal)> ZoneAsync(CancellationToken ct)
    {
        var site = await session.Query<Entry>()
            .Where(c => c.ContentType == barakoCMS.Features.Site.ShareLinks.ShareLinkKeys.SiteType
                        && c.Status == ContentStatus.Published)
            .OrderBy(c => c.CreatedAt)
            .Take(1)
            .FirstOrDefaultAsync(ct);

        var id = EntryUrls.Setting(site, "TimeZone");
        if (id is null) return (TimeZoneInfo.Utc, null);

        var zone = TemplateExpression.Zone(id);
        return zone is null
            ? (null, $"The site's time zone '{id}' is not one this server knows, so days cannot be told apart.")
            : (zone, null);
    }

    /// <summary>
    /// The type, the fields this caller may read in it, and the rows their Read rules show, or why
    /// the type cannot be aggregated for them.
    /// </summary>
    private async Task<(Scope? Scope, string? Refusal)> ScopeAsync(
        string contentType, User user, ClaimsPrincipal caller, CancellationToken ct)
    {
        var definition = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == contentType, ct);
        if (definition is null) return (null, $"Content type '{contentType}' does not exist.");

        var predicate = await permissions.ReadPredicateAsync(user, contentType, ct);
        if (!predicate.Compiled)
        {
            return (null, $"The caller's read rules for '{contentType}' depend on the entry in a way only a per-entry check can answer, "
                        + "so a figure cannot be taken in the database.");
        }

        // The lookup in SQL ignores the key's case, so a readable twin of a withheld name would read
        // the withheld value. A name is usable only when every field of that name is readable.
        var readable = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase);
        var withheld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in definition.Fields)
        {
            if (!TokenFields.IsToken(field.Type) && !InlineImageFields.Is(field.Type)
                && await sensitivity.MayReadFieldAsync(contentType, field, null, caller, ct))
                readable.TryAdd(field.Name, field);
            else
                withheld.Add(field.Name);
        }

        foreach (var name in withheld)
            readable.Remove(name);

        return (new Scope(definition, readable, predicate), null);
    }

    private async Task<IQueryable<Entry>> ScopedAsync(
        string contentType, ReadPredicate predicate, ClaimsPrincipal caller, CancellationToken ct)
    {
        var query = session.Query<Entry>().Where(c => c.ContentType == contentType);

        if (predicate.Sql != ReadPredicate.All.Sql)
            query = query.Where(c => c.MatchesSql(predicate.Sql!, predicate.Parameters));

        foreach (var level in new[] { SensitivityLevel.Sensitive, SensitivityLevel.Hidden })
        {
            if (await sensitivity.MaySeeDocumentAsync(level, caller, ct)) continue;

            var withheld = level;
            query = query.Where(c => c.Sensitivity != withheld);
        }

        return query;
    }

    private static Plan Fail(Plan plan, string reason)
    {
        plan.Refusal = reason;
        return plan;
    }

    private static string Unreadable(string field, string type) =>
        $"'{field}' is not a field this caller may use in '{type}'. A field has to be one the caller can read.";

    private static bool UsableName(string name) => NamePattern().IsMatch(name);

    private static bool IsDate(string? type) =>
        string.Equals(type, "date", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "datetime", StringComparison.OrdinalIgnoreCase);

    // ---- SQL --------------------------------------------------------------------------------

    private async Task<AggregateResult> ExecuteAsync(Plan plan, CancellationToken ct)
    {
        var grouped = plan.Groups.Count > 0;

        var rows = new List<Dictionary<string, object?>>();
        await using (var command = BuildCommand(plan, grouped))
        {
            command.CommandTimeout = 30;
            await using var reader = await session.ExecuteReaderAsync(command, ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < plan.Groups.Count; i++)
                    row[plan.Groups[i].Field] = reader.IsDBNull(i) ? null : reader.GetString(i);

                for (var i = 0; i < plan.Measures.Count; i++)
                {
                    var at = plan.Groups.Count + i;
                    row[plan.Measures[i].Alias] = reader.IsDBNull(at) ? null : Convert.ToDecimal(reader.GetValue(at), CultureInfo.InvariantCulture);
                }

                rows.Add(row);
            }
        }

        if (rows.Count > MaxGroups)
        {
            return AggregateResult.Refused(
                $"The query makes more than {MaxGroups} groups. Narrow it with a filter or a date range, or group on something coarser.");
        }

        if (!grouped)
            return new AggregateResult(rows, rows.Count > 0 ? rows[0] : new Dictionary<string, object?>(), null);

        var totals = new Dictionary<string, object?>(StringComparer.Ordinal);
        await using (var command = BuildCommand(plan, false))
        {
            command.CommandTimeout = 30;
            await using var reader = await session.ExecuteReaderAsync(command, ct);
            if (await reader.ReadAsync(ct))
            {
                for (var i = 0; i < plan.Measures.Count; i++)
                    totals[plan.Measures[i].Alias] = reader.IsDBNull(i) ? null : Convert.ToDecimal(reader.GetValue(i), CultureInfo.InvariantCulture);
            }
        }

        return new AggregateResult(rows, totals, null);
    }

    private static NpgsqlCommand BuildCommand(Plan plan, bool grouped)
    {
        var sql = new SqlBuilder();
        var header = sql.Embed(plan.Header.ToCommand(), "h");

        var select = new List<string>();
        if (grouped)
        {
            foreach (var group in plan.Groups)
                select.Add(GroupExpression(group, sql, plan.ZoneId));
        }

        var joins = new StringBuilder();
        var viaAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var via in plan.Measures.Where(m => m.Via is not null).Select(m => m.Via!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var alias = $"v{viaAlias.Count}";
            viaAlias[via] = alias;

            var target = sql.Embed(plan.Targets[via].ToCommand(), $"t{viaAlias.Count}");
            var again = sql.Embed(plan.Header.ToCommand(), $"r{viaAlias.Count}");

            var parts = new List<string> { "r.hid" };
            for (var i = 0; i < plan.Measures.Count; i++)
            {
                var m = plan.Measures[i];
                if (!string.Equals(m.Via, via, StringComparison.OrdinalIgnoreCase)) continue;

                parts.Add("count(*) AS n" + i);
                if (m.Field is null) continue;

                var number = Number(m.Field, "t");
                parts.Add($"COALESCE(sum({number}), 0) AS s{i}");
                parts.Add($"count({number}) AS c{i}");
                parts.Add($"min({number}) AS mn{i}");
                parts.Add($"max({number}) AS mx{i}");
            }

            joins.Append($" LEFT JOIN (SELECT {string.Join(", ", parts)} FROM "
                + $"(SELECT lower(d.data ->> 'Id') AS hid, unnest({References(via)}) AS ref FROM ({again}) AS d) r "
                + $"JOIN ({target}) AS t ON lower(t.data ->> 'Id') = r.ref GROUP BY r.hid) {alias} "
                + $"ON {alias}.hid = lower(d.data ->> 'Id')");
        }

        for (var i = 0; i < plan.Measures.Count; i++)
            select.Add(MeasureExpression(plan.Measures[i], i, viaAlias));

        var text = new StringBuilder($"SELECT {string.Join(", ", select)} FROM ({header}) AS d{joins}");

        var conditions = RangeConditions(plan, sql);
        if (grouped)
        {
            foreach (var group in plan.Groups.Where(g => g.Bucket is not null))
                conditions.Add($"{GroupExpression(group, sql, plan.ZoneId)} IS NOT NULL");
        }

        if (conditions.Count > 0) text.Append(" WHERE ").Append(string.Join(" AND ", conditions));

        if (grouped)
        {
            var ordinals = string.Join(", ", Enumerable.Range(1, plan.Groups.Count));
            text.Append($" GROUP BY {ordinals} ORDER BY {ordinals} LIMIT {MaxGroups + 1}");
        }

        sql.Command.CommandText = text.ToString();
        return sql.Command;
    }

    private static List<string> RangeConditions(Plan plan, SqlBuilder sql)
    {
        var conditions = new List<string>();
        if (plan.RangeField is null || (plan.RangeFrom is null && plan.RangeTo is null)) return conditions;

        var date = DateValue(plan.RangeField, "d", sql.Add(plan.ZoneId, NpgsqlDbType.Text));
        if (plan.RangeFrom is { } from)
            conditions.Add($"{date} >= {sql.Add(DateTime.SpecifyKind(from, DateTimeKind.Utc), NpgsqlDbType.TimestampTz)}");
        if (plan.RangeTo is { } to)
            conditions.Add($"{date} < {sql.Add(DateTime.SpecifyKind(to, DateTimeKind.Utc), NpgsqlDbType.TimestampTz)}");
        return conditions;
    }

    private static string GroupExpression(GroupPlan group, SqlBuilder sql, string zoneId)
    {
        if (group.Bucket is null) return Text(group.Field, "d");

        var format = group.Bucket switch { "day" => "YYYY-MM-DD", "month" => "YYYY-MM", _ => "YYYY" };
        return $"to_char({DateValue(group.Field, "d", sql.Add(zoneId, NpgsqlDbType.Text))} AT TIME ZONE {sql.Add(zoneId, NpgsqlDbType.Text)}, '{format}')";
    }

    private static string MeasureExpression(MeasurePlan m, int i, Dictionary<string, string> viaAlias)
    {
        if (m.Via is not null)
        {
            var v = viaAlias[m.Via];
            return m.Fn switch
            {
                "sum" => $"COALESCE(sum({v}.s{i}), 0)",
                "count" => m.Field is null ? $"COALESCE(sum({v}.n{i}), 0)" : $"COALESCE(sum({v}.c{i}), 0)",
                "avg" => $"sum({v}.s{i}) / NULLIF(sum({v}.c{i}), 0)",
                "min" => $"min({v}.mn{i})",
                _ => $"max({v}.mx{i})",
            };
        }

        if (m.Field is null) return "count(*)";

        var number = Number(m.Field, "d");
        return m.Fn switch
        {
            "sum" => $"COALESCE(sum({number}), 0)",
            "count" => $"count({number})",
            "avg" => $"avg({number})",
            "min" => $"min({number})",
            _ => $"max({number})",
        };
    }

    private static string Find(string field, string alias, string expression) =>
        $"(SELECT {expression} FROM jsonb_each({alias}.data -> 'Data') e WHERE lower(e.key) = lower('{field}') LIMIT 1)";

    private static string Text(string field, string alias) => Find(field, alias, "e.value #>> '{}'");

    private static string Number(string field, string alias) =>
        Find(field, alias, "CASE WHEN jsonb_typeof(e.value) = 'number' THEN (e.value #>> '{}')::numeric END");

    private const string Day = @"[1-9][0-9]{3}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])";
    private const string Clock = @"[T ]([01][0-9]|2[0-3]):[0-5][0-9](:[0-5][0-9]([.][0-9]{1,9})?)?";
    private const string Offset = @"(Z|[+-](0[0-9]|1[0-5]):?[0-5][0-9])";

    /// <summary>
    /// A stored date as an instant. A value with no zone on it is read in the site's zone, not the
    /// session's. A value that is not a real calendar date is null, so it is left out of buckets and
    /// ranges instead of failing the whole run.
    /// </summary>
    private static string DateValue(string field, string alias, string zone)
    {
        const string s = "(e.value #>> '{}')";
        const string inMonth =
            "substr(" + s + ", 9, 2)::int <= extract(day from ((substr(" + s + ", 1, 7) || '-01')::date + interval '1 month' - interval '1 day'))";

        return Find(field, alias,
            "CASE WHEN jsonb_typeof(e.value) = 'string' THEN CASE "
          + $"WHEN {s} ~ '^{Day}$' THEN CASE WHEN {inMonth} THEN ({s}::date)::timestamp AT TIME ZONE {zone} END "
          + $"WHEN {s} ~ '^{Day}{Clock}$' THEN CASE WHEN {inMonth} THEN {s}::timestamp AT TIME ZONE {zone} END "
          + $"WHEN {s} ~ '^{Day}{Clock}{Offset}$' THEN CASE WHEN {inMonth} THEN {s}::timestamptz END "
          + "END END");
    }

    /// <summary>The ids a reference field holds, lower case, whether it holds one or a list.</summary>
    private static string References(string field) =>
        Find(field, "d",
            "CASE jsonb_typeof(e.value) "
          + "WHEN 'array' THEN ARRAY(SELECT lower(x) FROM jsonb_array_elements_text(e.value) x) "
          + "WHEN 'string' THEN ARRAY[lower(e.value #>> '{}')] END");

    /// <summary>
    /// One command made of Marten's own commands and the parameters added here. Every parameter ends
    /// up named, so a numbered parameter of one part and a named one of another cannot mix.
    /// </summary>
    private sealed class SqlBuilder
    {
        public NpgsqlCommand Command { get; } = new();

        public string Add(object value, NpgsqlDbType type)
        {
            var parameter = new NpgsqlParameter($"agg{Command.Parameters.Count}", type) { Value = value };
            Command.Parameters.Add(parameter);

            var placeholder = "@" + parameter.ParameterName;
            return type == NpgsqlDbType.Text ? $"{placeholder}::text" : placeholder;
        }

        public string Embed(NpgsqlCommand part, string prefix)
        {
            var text = part.CommandText.TrimEnd().TrimEnd(';');

            if (PositionalParameter().IsMatch(text))
            {
                text = PositionalParameter().Replace(text, m => $"@{prefix}_p{m.Groups[1].Value}");
                var position = 0;
                foreach (NpgsqlParameter p in part.Parameters)
                {
                    var copy = (NpgsqlParameter)p.Clone();
                    copy.ParameterName = $"{prefix}_p{++position}";
                    Command.Parameters.Add(copy);
                }

                return text;
            }

            foreach (NpgsqlParameter p in part.Parameters.Cast<NpgsqlParameter>().OrderByDescending(p => p.ParameterName.Length).ToList())
            {
                var bare = p.ParameterName.TrimStart(':', '@');
                var renamed = prefix + "_" + bare;
                text = Regex.Replace(text, "([:@])" + Regex.Escape(bare) + @"\b", "$1" + renamed);

                var copy = (NpgsqlParameter)p.Clone();
                copy.ParameterName = renamed;
                Command.Parameters.Add(copy);
            }

            return text;
        }
    }
}
