using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Public;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.AspNetCore.Http;
using Npgsql;

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
internal sealed record QueryRunContext(IReadOnlyDictionary<string, string>? Values)
{
    /// <summary>
    /// True when every read is checked as <see cref="Actor"/>: the fields the query names, the type,
    /// each entry and each entry it follows to. False reads as the older runs always did.
    /// </summary>
    public bool AsActor { get; init; }

    /// <summary>The user the run reads as. <see cref="Guid.Empty"/> is nobody, and then only Public fields are read.</summary>
    public Guid Actor { get; init; }

    /// <summary>How long the rows may take before the statement is stopped. Null is <see cref="QueryRunner.StatementTimeout"/>.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>
/// The run a workflow step and the queries API use: parameters are bound as values and nothing in
/// the text of a query is read as anything else.
/// </summary>
internal interface IContextQueryRunner
{
    Task<QueryResult> RunAsync(QueryDefinition definition, QueryRunContext context, CancellationToken ct);

    Task<string?> ValidateAsync(QueryDefinition definition, QueryRunContext context, CancellationToken ct);
}

internal sealed class QueryRunner(
    IQuerySession session,
    IPermissionResolver? permissions = null,
    ISensitivityService? sensitivity = null,
    IBranchScopeGuard? branches = null,
    Microsoft.Extensions.Logging.ILogger<QueryRunner>? logger = null) : IQueryRunner, IContextQueryRunner
{
    /// <summary>How long the statement that reads the rows may run. A run that passes it is cancelled and answers with a message.</summary>
    public static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(15);

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
    private sealed record Checked(
        string? Refusal, ContentTypeDefinition? Schema, Dictionary<string, string> Allowed,
        Reader? Reader = null, Dictionary<string, Followed>? Follows = null);


    /// <summary>A selected <c>Reference.Field</c>: the reference field, the type it points at and the field read there.</summary>
    private sealed record Followed(FieldDefinition Reference, ContentTypeDefinition Target, FieldDefinition Field);

    /// <summary>Who the reads are checked as, with the services that decide what they may see.</summary>
    private sealed class Reader(User? user, IPermissionResolver permissions, ISensitivityService sensitivity, IBranchScopeGuard? branches)
    {
        public User? User { get; } = user;

        public ClaimsPrincipal Principal { get; } = user is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim("UserId", user.Id.ToString())], nameof(QueryRunner)));

        private HttpContext? _http;

        private HttpContext Http => _http ??= new DefaultHttpContext { User = Principal };

        /// <summary>
        /// Whether a field may be named, decided from the schema, the field rules and this reader's
        /// rights alone, never from a row. It passes only when the reader may read it on every entry
        /// of the type: the sensitivity of the field, the owner-hiding exemption (the one the read of
        /// an entry applies) and the fields the reader's read rules grant on every entry. With nobody
        /// to read as, only a Public field that no owner can hide.
        /// </summary>
        public async Task<bool> MayReadFieldAsync(string type, FieldDefinition field, CancellationToken ct) =>
            User is null
                ? field.Sensitivity == SensitivityLevel.Public && !barakoCMS.Core.Validation.PublicFields.IsOwnerHidden(field)
                : await sensitivity.MayReadFieldAsync(type, field, null, Principal, ct);

        /// <summary>
        /// The levels of document this reader may see, asked of the same service the read of an entry
        /// asks, so the query can leave the others out before the limit and the count.
        /// </summary>
        public async Task<List<SensitivityLevel>> HiddenLevelsAsync(CancellationToken ct)
        {
            var hidden = new List<SensitivityLevel>();
            foreach (var level in Enum.GetValues<SensitivityLevel>())
            {
                if (!await sensitivity.MaySeeDocumentAsync(level, Principal, ct)) hidden.Add(level);
            }

            return hidden;
        }

        public async Task<bool> MayReadTypeAsync(string type, CancellationToken ct) =>
            User is null || await permissions.CanPerformActionAsync(User, type, "read", null, ct);

        /// <summary>
        /// The entry as <c>GET /api/contents/{id}</c> shows it to this reader, or null when it shows
        /// nothing: no read rule grants it, or its whole document is hidden.
        /// </summary>
        public async Task<Dictionary<string, object>?> ShownAsync(Content entry, CancellationToken ct)
        {
            // The same order as the read of one entry: an entry of another branch is hidden as one that
            // does not exist, then the read rule, then the sensitivity of the document.
            if (branches is not null && !await branches.MayAccessAsync(Principal, entry, ct)) return null;
            if (User is not null && !await permissions.CanPerformActionAsync(User, entry.ContentType, "read", entry, ct)) return null;

            // An entry whose whole document is withheld is left out, whether the read would show it
            // as hidden or as a shell with its data cleared: either way this reader has no values to
            // match on, and a row that is present is a fact about them.
            if (!await sensitivity.MaySeeDocumentAsync(entry.Sensitivity, Principal, ct)) return null;

            var shown = new Dictionary<string, object>(entry.Data, StringComparer.OrdinalIgnoreCase);
            return await sensitivity.ApplyAsync(entry, shown, Http, ct) ? null : shown;
        }

        /// <summary>Whether the read shows the stored value of a field unchanged. A masked value, even one showing its last characters, is not a value this reader sees.</summary>
        public static bool Unchanged(Content entry, Dictionary<string, object> shown, string field)
        {
            foreach (var (key, stored) in entry.Data)
            {
                if (string.Equals(key, field, StringComparison.OrdinalIgnoreCase))
                {
                    return shown.TryGetValue(key, out var seen) && ReferenceEquals(stored, seen);
                }
            }

            return true;
        }
    }

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
        // When the run reads as a user, the allowlist is what that user may read, by the same service
        // that masks the same fields on a read. Without a user it is the Public fields.
        Reader? reader = null;
        if (context?.AsActor == true)
        {
            if (permissions is null || sensitivity is null)
            {
                return Refuse("Reads as a user cannot be checked here, so the query was not run.");
            }

            User? user = null;
            if (context.Actor != Guid.Empty)
            {
                user = await session.LoadAsync<User>(context.Actor, ct);
                if (user is null) return Refuse("The user this run reads as no longer exists.");
            }

            reader = new Reader(user, permissions, sensitivity, branches);
            if (!await reader.MayReadTypeAsync(schema.Name, ct))
            {
                return Refuse($"The user this run reads as may not read '{Safe(schema.Name)}'.");
            }
        }

        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in schema.Fields)
        {
            if (reader is null
                    ? f.Sensitivity == SensitivityLevel.Public && !barakoCMS.Core.Validation.PublicFields.IsOwnerHidden(f)
                    : await reader.MayReadFieldAsync(schema.Name, f, ct))
            {
                allowed[f.Name] = f.Type ?? string.Empty;
            }
        }

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

        if (!string.IsNullOrWhiteSpace(definition.SortField)
            && !allowed.ContainsKey(definition.SortField)
            && !(context is not null && IsSystemField(definition.SortField)))
        {
            return Refuse(Unknown(definition.SortField, allowed, schema.Name, "sorted on"));
        }

        if (definition.Fields.Count == 0)
        {
            // Refused rather than defaulting to everything. "All fields" is how a personal-data
            // field added next year ends up in a payload nobody revisited.
            return Refuse("Fields must name at least one field. A query with no projection would send whatever the schema grows.");
        }

        Dictionary<string, Followed>? follows = null;
        foreach (var field in definition.Fields)
        {
            if (field.Contains('.') && context is not null)
            {
                var (followed, refusal) = await FollowAsync(field, schema, allowed, reader, ct);
                if (refusal is not null) return Refuse(refusal);
                (follows ??= new(StringComparer.Ordinal))[field] = followed!;
                continue;
            }

            if (!allowed.ContainsKey(field) && !(context is not null && IsSystemField(field)))
            {
                return Refuse(Unknown(field, allowed, schema.Name, "returned from"));
            }
        }

        if (definition.Limit is < 1 or > QueryDefinition.MaxLimit)
        {
            return Refuse($"Limit must be between 1 and {QueryDefinition.MaxLimit}.");
        }

        return new Checked(null, schema, allowed, reader, follows);
    }

    /// <summary>
    /// A selected <c>Reference.Field</c>: one level, through a single-valued reference field this
    /// reader may read, to a field this reader may read on the type it points at.
    /// </summary>
    private async Task<(Followed? Followed, string? Refusal)> FollowAsync(
        string name, ContentTypeDefinition schema, Dictionary<string, string> allowed, Reader? reader, CancellationToken ct)
    {
        if (reader?.User is null)
        {
            return (null, $"'{Safe(name)}' follows a reference, which needs a user to read as, and this run has none.");
        }

        var parts = name.Split('.');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0
            || !barakoCMS.Core.Expressions.QueryText.IsFieldName(name, allowDot: true))
        {
            return (null, $"'{Safe(name)}' is not a field of '{schema.Name}'. Follow one reference, as Reference.Field.");
        }

        var reference = schema.Fields.FirstOrDefault(f => string.Equals(f.Name, parts[0], StringComparison.OrdinalIgnoreCase));
        if (reference is null || !allowed.ContainsKey(reference.Name)
            || !string.Equals(reference.Type, "reference", StringComparison.OrdinalIgnoreCase)
            || reference.Multiple || string.IsNullOrEmpty(reference.ReferenceType))
        {
            return (null, $"'{Safe(name)}' cannot be returned from '{schema.Name}'. Only a single reference field that names its type can be followed.");
        }

        var target = await session.Query<ContentTypeDefinition>().FirstNamedAsync(reference.ReferenceType, ct);
        var field = target?.Fields.FirstOrDefault(f => string.Equals(f.Name, parts[1], StringComparison.OrdinalIgnoreCase));
        if (target is null || field is null
            || !await reader.MayReadTypeAsync(target.Name, ct)
            || !await reader.MayReadFieldAsync(target.Name, field, ct))
        {
            return (null, $"'{Safe(name)}' cannot be returned from '{schema.Name}'. The field is not one the user this run reads as may read.");
        }

        return (new Followed(reference, target, field), null);
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
            var system = context is not null && IsSystemField(filter.Field);
            if (!system && !allowed.ContainsKey(filter.Field))
            {
                return Unknown(filter.Field, allowed, schema.Name, "filtered on");
            }

            if (!Ops.ContainsKey(filter.Op))
            {
                return $"Unknown operator '{Safe(filter.Op)}'. Supported: {string.Join(", ", Ops.Keys)}.";
            }

            if (system && SystemOps(filter.Field) is { } fits && !fits.Contains(Ops[filter.Op]))
            {
                return $"Operator '{Safe(filter.Op)}' does not apply to '{filter.Field}'.";
            }

            if (!system && lists.Contains(filter.Field) && Ops[filter.Op] is not (FilterOp.Eq or FilterOp.Ne))
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

        // An entry this reader may not see at all is not matched, counted or taken by the limit. It
        // is left out by its level, which no field value decides, so leaving it out says nothing
        // about any value. The read of each entry asks the same question again below.
        if (check.Reader is { } seeing)
        {
            foreach (var level in await seeing.HiddenLevelsAsync(ct))
            {
                var withheld = level;
                query = query.Where(c => c.Sensitivity != withheld);
            }
        }

        foreach (var filter in definition.Filters)
        {
            if (context is not null && IsSystemField(filter.Field))
            {
                var text = filter.Value;
                if (filter.Param is not null)
                {
                    var (raw, missing) = QueryParameters.Raw(filter.Param, context.Values);
                    if (missing is not null) return QueryResult.Refused(missing);
                    text = raw!;
                }

                var (narrowed, refusal) = SystemFilter(query, filter.Field, Ops[filter.Op], text, filter.Param);
                if (refusal is not null) return QueryResult.Refused(refusal);
                query = narrowed!;
                continue;
            }

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
            query = context is not null && IsSystemField(definition.SortField)
                ? SystemOrder(query, definition.SortField, definition.Descending)
                : query.OrderBySql(
                    DeliveryQuery.ToOrderBySql(new DeliverySort(canonical[definition.SortField], definition.Descending)));
        }
        else if (context is not null)
        {
            // A run with a context always has an order, so two runs of the same text over the same
            // entries return the same first row: newest first, and the id breaks a tie. A schema sort
            // above already ends on the id, and a system sort adds it.
            query = query.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id);
        }

        // Validated above, so the limit is already known to be within the ceiling. There used to be
        // a Math.Clamp here as a second guard, and it was unreachable: ValidateAsync refuses an
        // over-ceiling limit before this line runs. Refusing is the better answer anyway, because
        // silently returning a thousand rows of a query that asked for a hundred thousand is a lie
        // the caller cannot see. A clamp that cannot run is not defence in depth, it is a comment
        // that looks like a guard.
        var timeout = context?.Timeout ?? StatementTimeout;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);

        List<Content> rows;
        Dictionary<Guid, Content>? targets = null;
        try
        {
            if (check.Reader is not { } reader)
            {
                rows = [.. await query.Take(definition.Limit).ToListAsync(limit.Token)];
            }
            else
            {
                // The fields this reader may name were decided before any row was read, so no row can
                // change whether the run is refused. What is left to drop is an entry a read rule or
                // a branch withholds, and the limit is counted over the entries that are kept.
                var selected = definition.Fields.Select(f => f.Contains('.') ? f[..f.IndexOf('.')] : f)
                    .Where(n => !IsSystemField(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                rows = new List<Content>(Math.Min(definition.Limit, 64));
                var offset = 0;
                while (rows.Count < definition.Limit)
                {
                    var page = await query.Skip(offset).Take(definition.Limit).ToListAsync(limit.Token);
                    if (page.Count == 0) break;
                    offset += page.Count;

                    foreach (var row in page)
                    {
                        // An entry the reader may not read is left out, the same as one that is not there.
                        if (await reader.ShownAsync(row, limit.Token) is not { } shown) continue;

                        // A defence, not a rule: every selected field passed the check above, so a
                        // masked one means that check missed a case. Nothing is dropped silently.
                        foreach (var name in selected)
                        {
                            if (!Reader.Unchanged(row, shown, name)) return Missed(definition, name);
                        }

                        rows.Add(row);
                        if (rows.Count == definition.Limit) break;
                    }

                    if (page.Count < definition.Limit) break;
                }

                var followed = await FollowedEntriesAsync(rows, check.Follows, reader, limit.Token);
                targets = followed?.Entries;

                if (followed is not null && check.Follows is not null)
                {
                    foreach (var row in rows)
                    {
                        foreach (var follow in check.Follows.Values)
                        {
                            if (followed.Masked.Contains((ReferenceId(row, follow), follow.Field.Name.ToLowerInvariant())))
                            {
                                return Missed(definition, follow.Reference.Name + "." + follow.Field.Name);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested && TimedOut(e))
        {
            return QueryResult.Refused(
                $"The query took longer than {timeout.TotalSeconds:0.##} seconds and was stopped. Narrow it with a filter.");
        }

        var projected = new List<Dictionary<string, object>>(rows.Count);
        foreach (var row in rows)
        {
            var item = new Dictionary<string, object>(definition.Fields.Count, StringComparer.OrdinalIgnoreCase);

            foreach (var field in definition.Fields)
            {
                if (context is not null && IsSystemField(field))
                {
                    item[field] = SystemValue(row, field);
                    continue;
                }

                if (check.Follows?.TryGetValue(field, out var follow) == true)
                {
                    if (FollowedValue(row, follow, targets) is { } followedValue) item[field] = followedValue;
                    continue;
                }

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

    /// <summary>
    /// A selected field was shown masked on a row although the up-front check passed it. That check
    /// missed a case, so the run is refused and the miss is logged, with the field and not a value.
    /// </summary>
    private QueryResult Missed(QueryDefinition definition, string field)
    {
        logger?.LogWarning(
            "A saved query read '{Field}' of '{Type}' as a user who is shown it masked, although the check allowed it. The run was refused.",
            Safe(field), Safe(definition.ContentType));
        return QueryResult.Refused($"The user this run reads as may not read '{Safe(field)}', so the query was not used.");
    }

    /// <summary>Whether this is the cancellation of the statement by the run's own limit, whatever Marten wrapped it in.</summary>
    private static bool TimedOut(Exception e)
    {
        for (var inner = e; inner is not null; inner = inner.InnerException!)
        {
            if (inner is OperationCanceledException or PostgresException { SqlState: PostgresErrorCodes.QueryCanceled }) return true;
            if (inner.InnerException is null) break;
        }

        return false;
    }

    /// <summary>
    /// The entries the selected references point at, in one query, that the reader may read, each
    /// with the fields shown to them. An entry that is not there, not of the type the field names,
    /// or not readable is left out, so its value is absent, the same as for a template.
    /// </summary>
    private async Task<FollowedSet?> FollowedEntriesAsync(
        List<Content> rows, Dictionary<string, Followed>? follows, Reader reader, CancellationToken ct)
    {
        if (follows is null || rows.Count == 0) return null;

        var ids = rows
            .SelectMany(r => follows.Values.Select(f => ReferenceId(r, f)))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        if (ids.Length == 0) return new FollowedSet([], []);

        var found = await session.Query<Content>().Where(c => c.Id.In(ids)).ToListAsync(ct);
        var readable = new Dictionary<Guid, Content>(found.Count);
        var masked = new HashSet<(Guid, string)>();
        foreach (var entry in found)
        {
            if (await reader.ShownAsync(entry, ct) is not { } shown) continue;

            // Only the fields shown unchanged are kept, in a copy, so a masked value is never handed on.
            var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in entry.Data)
            {
                if (shown.TryGetValue(key, out var seen) && ReferenceEquals(value, seen)) data[key] = value;
                else masked.Add((entry.Id, key.ToLowerInvariant()));
            }

            readable[entry.Id] = new Content
            {
                Id = entry.Id, ContentType = entry.ContentType, Status = entry.Status, Data = data,
            };
        }

        return new FollowedSet(readable, masked);
    }

    /// <summary>The referenced entries the reader may read, and the fields of them that were shown masked, by id and lower-case name.</summary>
    private sealed record FollowedSet(Dictionary<Guid, Content> Entries, HashSet<(Guid, string)> Masked);

    private static Guid ReferenceId(Content row, Followed follow)
    {
        foreach (var (key, value) in row.Data)
        {
            if (!string.Equals(key, follow.Reference.Name, StringComparison.OrdinalIgnoreCase)) continue;

            var text = value is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element
                ? element.GetString()
                : value?.ToString();

            return Guid.TryParse(text, out var id) ? id : Guid.Empty;
        }

        return Guid.Empty;
    }

    private static object? FollowedValue(Content row, Followed follow, Dictionary<Guid, Content>? targets)
    {
        if (targets is null || !targets.TryGetValue(ReferenceId(row, follow), out var entry)
            || !string.Equals(entry.ContentType, follow.Target.Name, StringComparison.OrdinalIgnoreCase)) return null;

        foreach (var (key, value) in entry.Data)
        {
            if (string.Equals(key, follow.Field.Name, StringComparison.OrdinalIgnoreCase) && value is not null) return value;
        }

        return null;
    }

    // ---- the fields every entry has ---------------------------------------------------------

    /// <summary>Whether this is the exact spelling of a field every entry has: id, status, createdAt or updatedAt.</summary>
    private static bool IsSystemField(string name) => Array.IndexOf(barakoCMS.Core.Expressions.QueryText.SystemFields, name) >= 0;

    private static FilterOp[]? SystemOps(string field) => field switch
    {
        "id" or "status" => [FilterOp.Eq, FilterOp.Ne],
        _ => [FilterOp.Eq, FilterOp.Ne, FilterOp.Lt, FilterOp.Lte, FilterOp.Gt, FilterOp.Gte],
    };

    /// <summary>A system field's value in the form a step keeps it: an id, the status name, or a UTC time in round-trip form.</summary>
    private static string SystemValue(Content row, string field) => field switch
    {
        "id" => row.Id.ToString(),
        "status" => row.Status.ToString(),
        "createdAt" => row.CreatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        _ => row.UpdatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <remarks>
    /// Typed LINQ over the entry's own members, never a SQL fragment, so a system field adds no text
    /// to a statement. The value is parsed strictly; the message names the field, or the parameter
    /// when the value came from one, and never repeats the value.
    /// </remarks>
    private static (IQueryable<Content>? Query, string? Refusal) SystemFilter(
        IQueryable<Content> query, string field, FilterOp op, string text, string? param)
    {
        var what = param is null ? $"The value of the filter on '{field}'" : $"The value of @{param}";
        switch (field)
        {
            case "id":
                if (!Guid.TryParse(text.Trim(), out var id)) return (null, $"{what} is not an id.");
                return (op == FilterOp.Eq ? query.Where(c => c.Id == id) : query.Where(c => c.Id != id), null);

            case "status":
                var word = text.Trim();
                if (word.Length == 0 || char.IsDigit(word[0]) || !Enum.TryParse<ContentStatus>(word, ignoreCase: true, out var status))
                    return (null, $"{what} is not a status. A status is Draft, Published, Archived or Scheduled.");
                return (op == FilterOp.Eq ? query.Where(c => c.Status == status) : query.Where(c => c.Status != status), null);

            default:
                if (!DateTimeOffset.TryParse(
                        text.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var at))
                    return (null, $"{what} is not a date or a time.");

                var utc = at.UtcDateTime;
                return (field == "createdAt"
                    ? op switch
                    {
                        FilterOp.Eq => query.Where(c => c.CreatedAt == utc),
                        FilterOp.Ne => query.Where(c => c.CreatedAt != utc),
                        FilterOp.Lt => query.Where(c => c.CreatedAt < utc),
                        FilterOp.Lte => query.Where(c => c.CreatedAt <= utc),
                        FilterOp.Gt => query.Where(c => c.CreatedAt > utc),
                        _ => query.Where(c => c.CreatedAt >= utc),
                    }
                    : op switch
                    {
                        FilterOp.Eq => query.Where(c => c.UpdatedAt == utc),
                        FilterOp.Ne => query.Where(c => c.UpdatedAt != utc),
                        FilterOp.Lt => query.Where(c => c.UpdatedAt < utc),
                        FilterOp.Lte => query.Where(c => c.UpdatedAt <= utc),
                        FilterOp.Gt => query.Where(c => c.UpdatedAt > utc),
                        _ => query.Where(c => c.UpdatedAt >= utc),
                    }, null);
        }
    }

    /// <summary>A sort on a system field, with the id last so two entries never swap places between runs.</summary>
    private static IQueryable<Content> SystemOrder(IQueryable<Content> query, string field, bool descending) => (field, descending) switch
    {
        ("id", false) => query.OrderBy(c => c.Id),
        ("id", true) => query.OrderByDescending(c => c.Id),
        ("status", false) => query.OrderBy(c => c.Status).ThenBy(c => c.Id),
        ("status", true) => query.OrderByDescending(c => c.Status).ThenBy(c => c.Id),
        ("createdAt", false) => query.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id),
        ("createdAt", true) => query.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id),
        (_, false) => query.OrderBy(c => c.UpdatedAt).ThenBy(c => c.Id),
        _ => query.OrderByDescending(c => c.UpdatedAt).ThenBy(c => c.Id),
    };

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
    /// <summary>The value a run was given for the parameter, or why there is none. An empty value is not one.</summary>
    public static (string? Raw, string? Refusal) Raw(string name, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || !values.TryGetValue(name, out var raw) || raw is null)
        {
            return (null, $"The query needs a value for @{name}, and this run has none.");
        }

        return raw.Length == 0
            ? (null, $"The value of @{name} is empty, so it would match every entry that holds nothing in that field.")
            : (raw, null);
    }

    public static (string? Text, string? Refusal) Bind(string name, string declaredType, IReadOnlyDictionary<string, string>? values)
    {
        var (raw, missing) = Raw(name, values);
        if (missing is not null) return (null, missing);
        raw = raw!;

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
