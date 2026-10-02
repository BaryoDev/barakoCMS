using System.Text.Json;
using Marten;
using Marten.Linq;
using Marten.Linq.MatchesSql;
using Marten.Services;
using LogSafe = barakoCMS.Infrastructure.Logging.LogSafe;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// The half of the resolver that answers a condition following a reference, such as
/// <c>Class.InstructorUser</c>. See <see cref="ReferenceConditions"/> for what a path is and why
/// every way it can fail to resolve denies.
/// </summary>
/// <remarks>
/// A condition is answered one of three ways, and all three are the same question.
///
/// The first rows that ask, up to <see cref="ReferenceConditions.RowsReadSingly"/> of them, each
/// load the entry they point at: one read for each reference, which is all a get, an update, a
/// transition or the candidates of a slug need. A row past that means a pass over many rows, so
/// the condition is resolved once to the ids of the referenced entries that satisfy it and that
/// the caller may read, and every later row is a lookup in that set. The read
/// predicate for a list is built from the same set, so the page a list takes in the database and
/// the per-entry check over that page cannot disagree. What a pass costs does not grow with the
/// rows of other people it walks over.
///
/// The set holds at most <see cref="ReferenceConditions.MaxEntriesPerCondition"/> ids. A condition
/// that leads to more has none. Where the database can answer the whole condition, a list of a
/// named type is filtered by a subquery in place of the ids, and the rows of each page it returns
/// load the entries they point at. Any other pass over many rows is refused, with
/// <see cref="ReferenceConditionBoundException"/>, at the row that shows it is one: not answered
/// with the rows it reached, which would be a short list that says nothing about being short.
///
/// What is kept is kept for the DI scope, which is a request for every caller today, and dropped
/// when this scope's session commits, so a scope that writes an entry and then asks again reads it
/// again.
/// </remarks>
public partial class PermissionResolver
{
    private const int CompareBatch = 500;

    private readonly Dictionary<string, Models.ContentTypeDefinition?> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Models.Content?> _referenced = new();
    private readonly Dictionary<(string Type, string Field, string Operators), Followed> _followed = new();

    /// <summary>The rows this scope has asked a reference condition about.</summary>
    private readonly HashSet<Guid> _rows = new();

    private bool _followingReference;
    private bool _listening;

    /// <summary>A path that resolved against the content types: what to follow, to where, compared how.</summary>
    private sealed record ReferencePath(
        string Key,
        string ReferenceField,
        string TargetField,
        object Operators,
        Models.ContentTypeDefinition Target,
        ReadPredicate Comparison);

    /// <summary>What one condition has read so far in this scope.</summary>
    private sealed class Followed
    {
        /// <summary>Whether the set was asked for. It is asked for once.</summary>
        public bool Resolved { get; set; }

        /// <summary>
        /// The referenced entries that satisfy the condition and that the caller may read. Null
        /// until resolved, and null afterwards when there are more than a set holds.
        /// </summary>
        public HashSet<Guid>? Ids { get; set; }

        /// <summary>
        /// With no set: the whole condition as one fragment, when the database can answer it.
        /// </summary>
        public ReadPredicate? Matching { get; set; }

        /// <summary>
        /// Whether this scope handed a list a subquery for the condition, so the rows now being
        /// checked are a page the database already filtered.
        /// </summary>
        public bool Paged { get; set; }
    }

    private sealed class ForgetOnCommit(PermissionResolver resolver) : DocumentSessionListenerBase
    {
        public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
        {
            resolver.ForgetReferences();
            return Task.CompletedTask;
        }
    }

    private void ForgetReferences()
    {
        _definitions.Clear();
        _referenced.Clear();
        _followed.Clear();
        _rows.Clear();
    }

    private void ListenForCommits()
    {
        if (_listening)
            return;

        _listening = true;
        session.Listeners.Add(new ForgetOnCommit(this));
    }

    /// <summary>
    /// Whether a rule's conditions hold for this entry, following a reference where a key names one.
    /// </summary>
    /// <remarks>
    /// The entry's own conditions go to the evaluator as they always did.
    /// </remarks>
    private async Task<bool> GrantsAsync(
        Dictionary<string, object> conditions,
        Models.Content content,
        Models.User user,
        CancellationToken cancellationToken)
    {
        if (!ReferenceConditions.Mentioned(conditions))
            return conditionEvaluator.Evaluate(conditions, content, user, _profile);

        // One hop. This entry is being read to answer a reference, so a rule on it that follows
        // another grants nothing here. Its other rules are still asked.
        if (_followingReference)
        {
            NoteSecondHop(content.ContentType);
            return false;
        }

        var own = new Dictionary<string, object>();
        var paths = new List<KeyValuePair<string, object>>();

        foreach (var condition in conditions)
        {
            if (ReferenceConditions.IsPath(condition.Key))
                paths.Add(condition);
            else
                own[condition.Key] = condition.Value;
        }

        // The entry's own conditions first: they cost no read, and one failing spares the rest.
        if (own.Count > 0 && !conditionEvaluator.Evaluate(own, content, user, _profile))
            return false;

        foreach (var (key, operators) in paths)
        {
            if (!await ReferenceGrantsAsync(key, operators, content, user, cancellationToken))
                return false;
        }

        return true;
    }

    private async Task<bool> ReferenceGrantsAsync(
        string key,
        object? operators,
        Models.Content content,
        Models.User user,
        CancellationToken cancellationToken)
    {
        ListenForCommits();

        var path = await PathAsync(content.ContentType, key, operators, user, cancellationToken);
        if (path is null)
            return false;

        if (!content.Data.TryGetValue(path.ReferenceField, out var held)
            || !ReferenceConditions.TryReadId(held, out var targetId))
            return false;

        var followed = FollowedFor(path);

        if (followed.Ids is { } known)
            return known.Contains(targetId);

        // What makes a pass is the rows it asks about, not the entries they point at: one row may
        // hold two references into the same type, and that is still one row.
        _rows.Add(content.Id);

        // Past a handful of rows this is a pass over many. Resolve the condition once and answer
        // this row and every later one from the set.
        if (_rows.Count > ReferenceConditions.RowsReadSingly)
        {
            if (!followed.Resolved)
                await ResolveAsync(followed, path, user, cancellationToken);

            if (followed.Ids is { } resolved)
                return resolved.Contains(targetId);

            // No set. Only a page the database filtered by subquery goes on loading what its rows
            // point at, and a page is half the limit. Anything else is refused here, at the row
            // that shows the pass for what it is, before it reads further.
            if (!followed.Paged || _rows.Count > ReferenceConditions.MaxEntriesPerRequest)
                throw new ReferenceConditionBoundException(path.Key);
        }

        // Null for an entry that was erased or never existed, and for one in another tenant: the
        // session is this scope's, and it reads this tenant only.
        var target = await ReferencedAsync(targetId, cancellationToken);
        if (target is null
            || !string.Equals(target.ContentType, path.Target.Name, StringComparison.Ordinal)
            || target.Sensitivity != Models.SensitivityLevel.Public)
            return false;

        // The rule's answer says something about the referenced entry, so the caller has to be
        // allowed to read that entry under their own rules for it.
        if (!await ReadableAsync(user, target, cancellationToken))
            return false;

        return conditionEvaluator.Evaluate(
            new Dictionary<string, object> { [path.TargetField] = path.Operators }, target, user, _profile);
    }

    /// <summary>
    /// What a path names, or null when it names nothing that can be followed and compared.
    /// </summary>
    /// <remarks>
    /// Null also for a comparison the predicate compiler declines, such as a number where text is
    /// expected. A list is answered in the database, and a condition only the evaluator could
    /// answer would be one a caller can open and not list. A role write refuses such a condition.
    /// </remarks>
    private async Task<ReferencePath?> PathAsync(
        string contentType,
        string key,
        object? operators,
        Models.User user,
        CancellationToken cancellationToken)
    {
        if (!ReferenceConditions.TrySplit(key, out var referenceField, out var targetField)
            || operators is null
            || ReferenceConditions.OperatorNames(operators) is not { Count: > 0 })
            return null;

        var field = ReferenceConditions.ReferenceField(
            await DefinitionAsync(contentType, cancellationToken), referenceField);
        if (field is null)
            return null;

        Models.ContentTypeDefinition? target = null;
        foreach (var name in ReferenceConditions.TargetNames(field.ReferenceType!))
        {
            target = await DefinitionAsync(name, cancellationToken);
            if (target is not null)
                break;
        }

        if (target is null || !ReferenceConditions.IsComparable(target, targetField))
            return null;

        var comparison = ReferenceConditions.Comparison(targetField, operators, user.Id, _profile);
        if (comparison.Sql is null)
            return null;

        return new ReferencePath(key, referenceField, targetField, operators, target, comparison);
    }

    private Followed FollowedFor(ReferencePath path)
    {
        var key = (path.Target.Name, path.TargetField, JsonSerializer.Serialize(path.Operators));

        if (!_followed.TryGetValue(key, out var followed))
        {
            followed = new Followed();
            _followed[key] = followed;
        }

        return followed;
    }

    /// <summary>
    /// Reads the ids of the referenced entries that satisfy the condition and that the caller may
    /// read, through this scope's session, so the tenant filter is Marten's and not a clause
    /// written by hand.
    /// </summary>
    private async Task ResolveAsync(Followed followed, ReferencePath path, Models.User user, CancellationToken cancellationToken)
    {
        followed.Resolved = true;

        var roles = await RolesForAsync(user, cancellationToken);
        var targetType = path.Target.Name;
        var bound = ReferenceConditions.MaxEntriesPerCondition;

        var readable = PermissionPredicateCompiler.Compile(
            ReadRulesFollowingNothing(roles, targetType), user.Id, _profile);

        if (readable.Sql is not null)
        {
            var sql = $"({path.Comparison.Sql}) AND ({readable.Sql})";
            object[] parameters = [.. path.Comparison.Parameters, .. readable.Parameters];

            // One past the bound, which tells a condition that leads to exactly the bound from one
            // that leads to more.
            var ids = await session.Query<Models.Content>()
                .Where(c => c.ContentType == targetType && c.Sensitivity == Models.SensitivityLevel.Public)
                .Where(c => c.MatchesSql(sql, parameters))
                .OrderBy(c => c.Id)
                .Select(c => c.Id)
                .Take(bound + 1)
                .ToListAsync(cancellationToken);

            if (ids.Count > bound)
                followed.Matching = new ReadPredicate(sql, parameters);
            else
                followed.Ids = ids.ToHashSet();

            return;
        }

        // The caller's Read rules for the referenced type do not compile, a rule on $status for
        // one. The comparison still narrows in the database, and each match is asked the read
        // rules in memory, a batch at a time, so the bound counts what the caller may read and
        // not what matched. The whole entry is read, since a read rule may name any field of it.
        var comparisonSql = path.Comparison.Sql!;
        var comparisonParameters = path.Comparison.Parameters;
        var kept = new HashSet<Guid>();
        var compared = 0;
        Guid? last = null;

        while (true)
        {
            // One more than is left to compare, which tells exactly the limit from more than it.
            var remaining = ReferenceConditions.MaxEntriesCompared - compared;
            var take = Math.Min(CompareBatch, remaining + 1);

            // Each batch starts after the last id the one before it read. An offset would rescan
            // what was read, and an entry erased between two batches would shift the rest and
            // leave one unread.
            var sql = last is null ? comparisonSql : $"({comparisonSql}) AND d.id > ?";
            var parameters = comparisonParameters;
            if (last is { } after)
                parameters = [.. comparisonParameters, after];

            var batch = await session.Query<Models.Content>()
                .Where(c => c.ContentType == targetType && c.Sensitivity == Models.SensitivityLevel.Public)
                .Where(c => c.MatchesSql(sql, parameters))
                .OrderBy(c => c.Id)
                .Take(take)
                .ToListAsync(cancellationToken);

            if (batch.Count > remaining)
                return;

            foreach (var match in batch)
            {
                if (await ReadableAsync(user, match, cancellationToken))
                    kept.Add(match.Id);
            }

            compared += batch.Count;

            if (kept.Count > bound)
                return;

            if (batch.Count < take)
            {
                followed.Ids = kept;
                return;
            }

            last = batch[^1].Id;
        }
    }

    private async Task<bool> ReadableAsync(Models.User user, Models.Content target, CancellationToken cancellationToken)
    {
        _followingReference = true;
        try
        {
            return await CanPerformActionAsync(user, target.ContentType, "read", target, cancellationToken);
        }
        finally
        {
            _followingReference = false;
        }
    }

    /// <summary>
    /// The read predicate for rules of which at least one follows a reference: each such condition
    /// becomes "the reference field holds one of these ids", or, past what a set holds, "the
    /// reference field holds an id this query selects".
    /// </summary>
    private async Task<ReadPredicate> CompileFollowingReferencesAsync(
        IReadOnlyList<Models.Role> roles,
        List<Models.PermissionRule> rules,
        string contentTypeSlug,
        Models.User user,
        CancellationToken cancellationToken)
    {
        // One unconditional grant is the whole union, and nothing needs reading to say so.
        if (rules.Any(r => r.Enabled && (r.Conditions is null || r.Conditions.Count == 0)))
            return ReadPredicate.All;

        ListenForCommits();

        var references = new Dictionary<Models.PermissionRule, IReadOnlyDictionary<string, ReadPredicate>>();
        var bySubquery = new List<(Followed Followed, string Key)>();

        foreach (var rule in rules)
        {
            if (!rule.Enabled || !ReferenceConditions.Mentioned(rule.Conditions))
                continue;

            var clauses = new Dictionary<string, ReadPredicate>();

            foreach (var (key, operators) in rule.Conditions!)
            {
                if (!ReferenceConditions.IsPath(key))
                    continue;

                var path = await PathAsync(contentTypeSlug, key, operators, user, cancellationToken);
                if (path is null)
                {
                    clauses[key] = ReadPredicate.Nothing;
                    continue;
                }

                var followed = FollowedFor(path);
                if (!followed.Resolved)
                    await ResolveAsync(followed, path, user, cancellationToken);

                if (followed.Ids is { } ids)
                {
                    clauses[key] = ReferenceConditions.In(path.ReferenceField, ids);
                    continue;
                }

                // No set. The database answers the condition whole or the list is refused, before
                // the endpoint loads the type to check it row by row.
                clauses[key] = Subquery(path, followed) ?? throw new ReferenceConditionBoundException(path.Key);
                bySubquery.Add((followed, path.Key));
            }

            references[rule] = clauses;
        }

        var predicate = PermissionPredicateCompiler.Compile(rules, user.Id, _profile, references);

        // With no predicate the endpoint would load the whole type and check it row by row, which
        // a condition with no set cannot answer. Refused now, before that load.
        if (!predicate.Compiled && bySubquery.Count > 0)
            throw new ReferenceConditionBoundException(bySubquery[0].Key);

        // The rows this scope checks next are a page the database filtered.
        foreach (var (followed, _) in bySubquery)
            followed.Paged = true;

        return predicate;
    }

    /// <summary>
    /// The condition as "the reference field holds an id this query selects", or null when the
    /// database cannot answer it whole.
    /// </summary>
    /// <remarks>
    /// The query is built on this scope's session and rendered by Marten, tenant filter included,
    /// from the same fragments the set is read with. Nothing here names the table or its tenant
    /// column.
    /// </remarks>
    private ReadPredicate? Subquery(ReferencePath path, Followed followed)
    {
        if (followed.Matching is not { Sql: { } sql } matching)
            return null;

        var targetType = path.Target.Name;
        var parameters = matching.Parameters;

        var command = session.Query<Models.Content>()
            .Where(c => c.ContentType == targetType && c.Sensitivity == Models.SensitivityLevel.Public)
            .Where(c => c.MatchesSql(sql, parameters))
            .Select(c => c.Id)
            .ToCommand(FetchType.FetchMany);

        return ReferenceConditions.InSubquery(path.ReferenceField, command);
    }

    /// <summary>
    /// The caller's read rules for a referenced type, leaving out any that follows a reference
    /// itself. Gathered the way <see cref="CanPerformActionAsync"/> gathers them.
    /// </summary>
    private List<Models.PermissionRule> ReadRulesFollowingNothing(
        IReadOnlyList<Models.Role> roles, string contentTypeSlug)
    {
        var rules = new List<Models.PermissionRule>();

        foreach (var role in roles)
        {
            var permission = role.Permissions.FirstOrDefault(p => p.ContentTypeSlug == contentTypeSlug);
            if (permission is null)
                continue;

            var rule = GetRuleForAction(permission, "read");
            if (rule is null)
                continue;

            if (rule.Enabled && ReferenceConditions.Mentioned(rule.Conditions))
            {
                NoteSecondHop(contentTypeSlug);
                continue;
            }

            rules.Add(rule);
        }

        return rules;
    }

    private async Task<Models.ContentTypeDefinition?> DefinitionAsync(string name, CancellationToken cancellationToken)
    {
        if (_definitions.TryGetValue(name, out var known))
            return known;

        var definition = await ReferenceConditions.DefinitionAsync(session, name, cancellationToken);
        _definitions[name] = definition;
        return definition;
    }

    /// <summary>The entry a reference points at, read once per scope.</summary>
    private async Task<Models.Content?> ReferencedAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_referenced.TryGetValue(id, out var known))
            return known;

        var target = await session.LoadAsync<Models.Content>(id, cancellationToken);
        _referenced[id] = target;
        return target;
    }

    /// <summary>
    /// Debug, not a warning: a Read rule on the referenced type that itself follows a reference is
    /// a legitimate setup, and it would be logged on every request by such a caller.
    /// </summary>
    private void NoteSecondHop(string contentType)
    {
        logger?.LogDebug(
            "A condition followed a reference into {ContentType}, whose own read rule follows another. "
          + "One reference is followed, so that rule grants nothing to the condition.",
            LogSafe.Value(contentType));
    }
}
