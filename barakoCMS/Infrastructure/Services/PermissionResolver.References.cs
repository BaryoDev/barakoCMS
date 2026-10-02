using System.Text.Json;
using Marten;
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
/// A condition is answered one of two ways, and both are the same question.
///
/// The first row that asks loads the entry it points at: one read, which is all a get, an update
/// or a transition needs. The second row that asks, pointing somewhere else, means a pass over
/// many rows, so the condition is resolved once to the ids of the referenced entries that satisfy
/// it and that the caller may read, and every later row is a lookup in that set. The read
/// predicate for a list is built from the same set, so the page a list takes in the database and
/// the per-entry check over that page cannot disagree. What a pass costs does not grow with the
/// rows of other people it walks over.
///
/// The set holds at most <see cref="ReferenceConditions.MaxEntriesPerCondition"/> ids. A condition
/// that matches more has no set: the list is not paged in the database, and each row loads the
/// entry it points at, up to <see cref="ReferenceConditions.MaxEntriesPerRequest"/> distinct
/// entries. Past that the check throws rather than deny, because a denial there would be a short
/// list that says nothing about being short.
///
/// What is kept is kept for the DI scope, which is a request for every caller today, and dropped
/// when this scope's session commits, so a scope that writes an entry and then asks again reads it
/// again.
/// </remarks>
public partial class PermissionResolver
{
    private readonly Dictionary<string, Models.ContentTypeDefinition?> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Models.Content?> _referenced = new();
    private readonly Dictionary<(string Type, string Field, string Operators), Followed> _followed = new();

    private bool _followingReference;
    private bool _listening;
    private bool _warnedSecondHop;

    /// <summary>A path that resolved against the content types: what to follow, to where, compared how.</summary>
    private sealed record ReferencePath(
        string ReferenceField,
        string TargetField,
        object Operators,
        Models.ContentTypeDefinition Target,
        ReadPredicate Comparison);

    /// <summary>What one condition has read so far in this scope.</summary>
    private sealed class Followed
    {
        /// <summary>Whether a row has already loaded the entry it points at for this condition.</summary>
        public bool Loaded { get; set; }

        /// <summary>Whether the set was asked for. It is asked for once.</summary>
        public bool Resolved { get; set; }

        /// <summary>
        /// The referenced entries that satisfy the condition and that the caller may read. Null
        /// until resolved, and null afterwards when more match than a set holds.
        /// </summary>
        public HashSet<Guid>? Ids { get; set; }
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
            WarnSecondHop(content.ContentType);
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

        // A second row pointing somewhere new is a pass over many rows. Resolve the condition once
        // and answer this row and every later one from the set.
        if (followed.Loaded && !followed.Resolved && !_referenced.ContainsKey(targetId))
        {
            await ResolveAsync(followed, path, user, cancellationToken);

            if (followed.Ids is { } resolved)
                return resolved.Contains(targetId);
        }

        followed.Loaded = true;

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

        return new ReferencePath(referenceField, targetField, operators, target, comparison);
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

            // One past the bound, which tells a condition that matches exactly the bound from one
            // that matches more.
            var ids = await session.Query<Models.Content>()
                .Where(c => c.ContentType == targetType && c.Sensitivity == Models.SensitivityLevel.Public)
                .Where(c => c.MatchesSql(sql, parameters))
                .OrderBy(c => c.Id)
                .Select(c => c.Id)
                .Take(bound + 1)
                .ToListAsync(cancellationToken);

            if (ids.Count > bound)
                WarnTooMany(path);
            else
                followed.Ids = ids.ToHashSet();

            return;
        }

        // The caller's Read rules for the referenced type do not compile, a rule on $status for
        // one. The comparison still narrows in the database, and each match is asked the read
        // rules in memory.
        var comparisonSql = path.Comparison.Sql!;
        var comparisonParameters = path.Comparison.Parameters;

        var matches = await session.Query<Models.Content>()
            .Where(c => c.ContentType == targetType && c.Sensitivity == Models.SensitivityLevel.Public)
            .Where(c => c.MatchesSql(comparisonSql, comparisonParameters))
            .OrderBy(c => c.Id)
            .Take(bound + 1)
            .ToListAsync(cancellationToken);

        if (matches.Count > bound)
        {
            WarnTooMany(path);
            return;
        }

        var kept = new HashSet<Guid>();
        foreach (var match in matches)
        {
            if (await ReadableAsync(user, match, cancellationToken))
                kept.Add(match.Id);
        }

        followed.Ids = kept;
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
    /// becomes "the reference field holds one of these ids".
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

                // More match than a set holds, so there is no predicate and the list is answered
                // per entry, exactly or not at all.
                if (followed.Ids is not { } ids)
                    return ReadPredicate.None;

                clauses[key] = ReferenceConditions.In(path.ReferenceField, ids);
            }

            references[rule] = clauses;
        }

        return PermissionPredicateCompiler.Compile(rules, user.Id, _profile, references);
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
                WarnSecondHop(contentTypeSlug);
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

    /// <summary>
    /// The entry a reference points at, read once per scope. Throws past
    /// <see cref="ReferenceConditions.MaxEntriesPerRequest"/> distinct entries.
    /// </summary>
    /// <remarks>
    /// Reached only by the first row of each condition and by a condition with no set. A caller
    /// walking a whole collection gets an error there and not a list with rows quietly left out.
    /// </remarks>
    private async Task<Models.Content?> ReferencedAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_referenced.TryGetValue(id, out var known))
            return known;

        if (_referenced.Count >= ReferenceConditions.MaxEntriesPerRequest)
        {
            throw new ReferenceConditionBoundException(
                "A permission condition that follows a reference matches more than "
              + $"{ReferenceConditions.MaxEntriesPerCondition} entries, so each row's reference is read on its own, "
              + $"and this request needed more than {ReferenceConditions.MaxEntriesPerRequest} of them. "
              + "Narrow the condition, or name fewer rows in the request.");
        }

        var target = await session.LoadAsync<Models.Content>(id, cancellationToken);
        _referenced[id] = target;
        return target;
    }

    private void WarnTooMany(ReferencePath path)
    {
        logger?.LogWarning(
            "A condition on {Field} matches more than {Bound} entries of {ContentType} that the caller may read. "
          + "A list is not paged in the database for it, and each row's reference is read on its own.",
            LogSafe.Value(path.TargetField),
            ReferenceConditions.MaxEntriesPerCondition,
            LogSafe.Value(path.Target.Name));
    }

    private void WarnSecondHop(string contentType)
    {
        if (_warnedSecondHop)
            return;

        _warnedSecondHop = true;
        logger?.LogWarning(
            "A condition followed a reference into {ContentType}, whose own read rule follows another. "
          + "One reference is followed, so that rule grants nothing to the condition.",
            LogSafe.Value(contentType));
    }
}
