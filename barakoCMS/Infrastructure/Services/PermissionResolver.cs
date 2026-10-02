using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Service for resolving user permissions using additive (union) role semantics: a user is
/// granted an action if ANY of their roles grants it. The seeded SuperAdmin role bypasses all
/// checks, identified by its id rather than its name: the name is not the key, and a custom role
/// that took it used to inherit the bypass. See Models/SystemRoles.
/// </summary>
public class PermissionResolver(
    IDocumentSession session,
    IConditionEvaluator conditionEvaluator,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    ILogger<PermissionResolver>? logger = null) : IPermissionResolver
{
    /// <summary>The user's roles in this tenant, read once per request.</summary>
    /// <remarks>
    /// Two queries per call, and every one of them asked the same question. The entries list checks
    /// permission on every entry it loaded, so a tenant with fifty thousand of them issued a hundred
    /// thousand queries to return a page of twenty. The decision cache above this does not help: its
    /// key includes the item id, so a first pass over a list is a miss on every row.
    ///
    /// Cached on the instance, which is scoped to the request. Roles that change mid-request are
    /// deliberately not seen, and that is the correct answer rather than a compromise: a list where
    /// row nine thousand was judged under different roles than row one is not a list of anything.
    /// </remarks>
    private IReadOnlyList<Models.Role>? _roles;
    private Guid _rolesFor;

    /// <summary>
    /// The caller's member profile in this tenant, from the same membership row the roles came
    /// from, so it costs no query of its own. Null when the user has no active membership here.
    /// </summary>
    /// <remarks>
    /// Read from the database on every request and never from the token, so a changed attribute
    /// applies to the next request and a removed one stops granting then.
    /// </remarks>
    private IReadOnlyDictionary<string, string>? _profile;

    /// <summary>
    /// What following references has read so far, kept for the request so a list asks once for a
    /// type and once for an entry many rows point at.
    /// </summary>
    private readonly Dictionary<string, Models.ContentTypeDefinition?> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Models.Content?> _referenced = new();

    private bool _followingReference;
    private bool _warnedPastBound;
    private bool _warnedSecondHop;

    private async Task<IReadOnlyList<Models.Role>> RolesForAsync(Models.User user, CancellationToken cancellationToken)
    {
        if (_roles is not null && _rolesFor == user.Id)
        {
            return _roles;
        }

        // Roles come from the user's membership in the current tenant (falling back to the user's
        // legacy roles when there's no membership).
        var (roleIds, membership) = await barakoCMS.Infrastructure.Multitenancy.MembershipRoles
            .ResolveAsync(session, user, tenant.Slug, cancellationToken);

        IReadOnlyList<Models.Role> roles = roleIds.Count == 0
            ? Array.Empty<Models.Role>()
            : await session.Query<Models.Role>()
                .Where(r => r.Id.In(roleIds))
                .ToListAsync(cancellationToken);

        _rolesFor = user.Id;
        _roles = roles;
        _profile = membership?.Profile;

        return roles;
    }

    /// <summary>
    /// Check if a user can perform an action using additive (union) logic: access is granted if
    /// ANY of the user's roles has an enabled rule for the action whose conditions match.
    /// </summary>
    public async Task<bool> CanPerformActionAsync(
        Models.User user,
        string contentTypeSlug,
        string action,
        Models.Content? content = null,
        CancellationToken cancellationToken = default)
    {
        var roles = await RolesForAsync(user, cancellationToken);
        if (roles.Count == 0)
            return false;

        // SUPER ADMIN BYPASS
        if (roles.Any(r => r.Id == Models.SystemRoles.SuperAdminRoleId))
            return true;

        // Get permission rules for this content type + action
        var rules = new List<Models.PermissionRule>();
        foreach (var role in roles)
        {
            var permission = role.Permissions
                .FirstOrDefault(p => p.ContentTypeSlug == contentTypeSlug);

            if (permission != null)
            {
                var rule = GetRuleForAction(permission, action);
                if (rule != null)
                    rules.Add(rule);
            }
        }

        // No rules = no permission
        if (rules.Count == 0)
            return false;

        // ADDITIVE LOGIC (Union): If ANY rule allows, grant access.
        // Unless we explicitly need restrictive (intersection), Additive is standard for CMS.
        // Rules that follow no reference first: they read nothing, and one of them granting spares
        // the loads the others would make.
        foreach (var rule in rules.OrderBy(r => ReferenceConditions.Mentioned(r.Conditions)))
        {
            // If rule is enabled...
            if (rule.Enabled)
            {
                // And conditions match (or are empty)...
                // Passing the document rather than only its data bag is what lets a rule name
                // $createdBy, which is where ownership lives. Without it an ownership condition
                // would resolve to "field not present" and deny every record including the caller's
                // own, which reads as a broken rule rather than as a missing capability.
                if (content == null || rule.Conditions == null || rule.Conditions.Count == 0 ||
                    await GrantsAsync(rule.Conditions, content, user, cancellationToken))
                {
                    return true; // Granted by at least one role
                }
            }
        }

        // None of the rules granted access
        return false;
    }

    /// <inheritdoc />
    public async Task<ReadPredicate> ReadPredicateAsync(
        Models.User user,
        string contentTypeSlug,
        CancellationToken cancellationToken = default)
    {
        var roles = await RolesForAsync(user, cancellationToken);

        // No roles at all is not "no predicate", it is no rows, and saying so lets the caller skip
        // the query rather than load a collection to deny every item in it.
        if (roles.Count == 0) return ReadPredicate.Nothing;

        if (roles.Any(r => r.Id == Models.SystemRoles.SuperAdminRoleId)) return ReadPredicate.All;

        // Gathered exactly the way CanPerformActionAsync gathers them, because the two have to be
        // looking at the same set for the agreement property to mean anything.
        var rules = new List<Models.PermissionRule>();
        foreach (var role in roles)
        {
            var permission = role.Permissions.FirstOrDefault(p => p.ContentTypeSlug == contentTypeSlug);
            if (permission is null) continue;

            var rule = GetRuleForAction(permission, "read");
            if (rule is not null) rules.Add(rule);
        }

        if (!rules.Any(r => r.Enabled && ReferenceConditions.Mentioned(r.Conditions)))
            return PermissionPredicateCompiler.Compile(rules, user.Id, _profile);

        return await CompileFollowingReferencesAsync(roles, rules, contentTypeSlug, user, cancellationToken);
    }

    /// <summary>
    /// Resolves the union of the caller's roles' <see cref="Models.Role.SystemCapabilities"/>.
    /// SuperAdmin bypasses, the same way it does for content permissions.
    /// </summary>
    public async Task<bool> HasCapabilityAsync(
        Guid userId,
        string capability,
        CancellationToken cancellationToken = default)
    {
        var user = await session.LoadAsync<Models.User>(userId, cancellationToken);
        if (user is null)
            return false;

        var roleIds = await barakoCMS.Infrastructure.Multitenancy.MembershipRoles
            .EffectiveRoleIdsAsync(session, user, tenant.Slug, cancellationToken);
        if (roleIds.Count == 0)
            return false;

        var roles = await session.Query<Models.Role>()
            .Where(r => r.Id.In(roleIds))
            .ToListAsync(cancellationToken);

        if (roles.Count == 0)
            return false;

        if (roles.Any(r => r.Id == Models.SystemRoles.SuperAdminRoleId))
            return true;

        return roles.Any(r => Models.SystemCapabilities.Satisfies(r.SystemCapabilities, capability));
    }

    // No caching in the inner resolver, so invalidation is a no-op here. The CachedPermissionResolver
    // decorator implements the actual eviction.
    public void InvalidateUserPermissions(Guid userId) { }

    public void InvalidateAllPermissions() { }

    /// <summary>The prefix an action uses to name a lifecycle transition rather than a CRUD verb.</summary>
    /// <remarks>
    /// Prefixed so a transition can never collide with a CRUD action, whatever somebody names it. A
    /// content type declaring a transition called "Update" would otherwise silently reuse the CRUD
    /// rule, and the collision would look like a permission that mysteriously already applied.
    /// </remarks>
    public const string TransitionActionPrefix = "transition:";

    private Models.PermissionRule? GetRuleForAction(Models.ContentTypePermission permission, string action)
    {
        if (action.StartsWith(TransitionActionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = action[TransitionActionPrefix.Length..];

            // Compared here rather than trusting the dictionary's comparer. Transitions is built
            // with StringComparer.OrdinalIgnoreCase, and that comparer does not survive the trip
            // through the database: System.Text.Json constructs a fresh Dictionary with the default
            // comparer when it deserialises the role, so a rule saved as "approve" would stop
            // matching a transition named "Approve" once the document was reloaded. The failure is
            // a 403 on a permission the operator can see granted in the admin UI.
            foreach (var candidate in permission.Transitions)
            {
                if (string.Equals(candidate.Key, name, StringComparison.OrdinalIgnoreCase))
                    return candidate.Value;
            }

            // Missing means refused, not inherited from Update. Returning the Update rule here is the
            // obvious way to keep existing configurations working and it is exactly the defect: it
            // grants approval to everyone who can edit.
            return null;
        }

        return action.ToLower() switch
        {
            "create" => permission.Create,
            "read" => permission.Read,
            "update" => permission.Update,
            "delete" => permission.Delete,
            _ => null
        };
    }

    /// <summary>
    /// Whether a rule's conditions hold for this entry, following a reference where a key names one.
    /// </summary>
    /// <remarks>
    /// The entry's own conditions go to the evaluator as they always did. A key that follows a
    /// reference is answered here, because it takes a read: see <see cref="ReferenceConditions"/>
    /// for what a path is and why every way it can fail denies.
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

        // The entry's own conditions first: they cost no read, and one failing spares the load.
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
        if (!ReferenceConditions.TrySplit(key, out var referenceField, out var targetField)
            || ReferenceConditions.OperatorNames(operators) is not { Count: > 0 })
            return false;

        var field = ReferenceConditions.ReferenceField(
            await DefinitionAsync(content.ContentType, cancellationToken), referenceField);
        if (field is null)
            return false;

        var targetDefinition = await TargetDefinitionAsync(field.ReferenceType!, cancellationToken);
        if (targetDefinition is null || !ReferenceConditions.IsComparable(targetDefinition, targetField))
            return false;

        if (!content.Data.TryGetValue(referenceField, out var held) || !ReferenceConditions.TryReadId(held, out var targetId))
            return false;

        // Null for an entry that was erased or never existed, and for one in another tenant: the
        // session is the request's, and it reads this tenant only.
        var target = await ReferencedAsync(targetId, cancellationToken);
        if (target is null
            || !string.Equals(target.ContentType, targetDefinition.Name, StringComparison.Ordinal)
            || target.Sensitivity != Models.SensitivityLevel.Public)
            return false;

        // The rule's answer says something about the referenced entry, so the caller has to be
        // allowed to read that entry under their own rules for it.
        bool readable;
        _followingReference = true;
        try
        {
            readable = await CanPerformActionAsync(user, target.ContentType, "read", target, cancellationToken);
        }
        finally
        {
            _followingReference = false;
        }

        if (!readable)
            return false;

        return conditionEvaluator.Evaluate(
            new Dictionary<string, object> { [targetField] = operators! }, target, user, _profile);
    }

    /// <summary>
    /// The read predicate for rules of which at least one follows a reference.
    /// </summary>
    /// <remarks>
    /// Each such condition becomes "the reference field holds one of these ids", where the ids are
    /// the referenced entries that satisfy the condition and that the caller may read. They are
    /// read here, through the request's session, so the tenant filter is Marten's and not a clause
    /// written by hand. The list then pages and counts in the database, and the per-entry check
    /// over the page it returns is the one in <see cref="GrantsAsync"/>.
    /// </remarks>
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

                var clause = await ReferenceClauseAsync(roles, contentTypeSlug, key, operators, user, cancellationToken);

                // Not compilable, so the list is answered per entry, by GrantsAsync.
                if (!clause.Compiled)
                    return ReadPredicate.None;

                clauses[key] = clause;
            }

            references[rule] = clauses;
        }

        return PermissionPredicateCompiler.Compile(rules, user.Id, _profile, references);
    }

    private async Task<ReadPredicate> ReferenceClauseAsync(
        IReadOnlyList<Models.Role> roles,
        string contentTypeSlug,
        string key,
        object? operators,
        Models.User user,
        CancellationToken cancellationToken)
    {
        if (!ReferenceConditions.TrySplit(key, out var referenceField, out var targetField)
            || ReferenceConditions.OperatorNames(operators) is not { Count: > 0 })
            return ReadPredicate.Nothing;

        var field = ReferenceConditions.ReferenceField(
            await DefinitionAsync(contentTypeSlug, cancellationToken), referenceField);
        if (field is null)
            return ReadPredicate.Nothing;

        var targetDefinition = await TargetDefinitionAsync(field.ReferenceType!, cancellationToken);
        if (targetDefinition is null || !ReferenceConditions.IsComparable(targetDefinition, targetField))
            return ReadPredicate.Nothing;

        var targetType = targetDefinition.Name;

        var comparison = PermissionPredicateCompiler.Compile(
            [new Models.PermissionRule { Enabled = true, Conditions = new() { [targetField] = operators! } }],
            user.Id,
            _profile);

        var readable = PermissionPredicateCompiler.Compile(
            ReadRulesFollowingNothing(roles, targetType), user.Id, _profile);

        if (comparison.Sql is null || readable.Sql is null)
            return ReadPredicate.None;

        var sql = $"({comparison.Sql}) AND ({readable.Sql})";
        object[] parameters = [.. comparison.Parameters, .. readable.Parameters];

        // One past the bound, which tells a condition that matches exactly the bound from one that
        // matches more.
        var ids = await session.Query<Models.Content>()
            .Where(c => c.ContentType == targetType && c.Sensitivity == Models.SensitivityLevel.Public)
            .Where(c => c.MatchesSql(sql, parameters))
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .Take(ReferenceConditions.MaxEntriesPerCondition + 1)
            .ToListAsync(cancellationToken);

        if (ids.Count > ReferenceConditions.MaxEntriesPerCondition)
        {
            logger?.LogWarning(
                "The condition {Condition} matches more than {Bound} entries of {ContentType}. A list answered "
              + "in the database follows at most that many, so the condition matches nothing in this list.",
                key, ReferenceConditions.MaxEntriesPerCondition, targetType);

            return ReadPredicate.Nothing;
        }

        return ReferenceConditions.In(referenceField, ids);
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

    private Task<Models.ContentTypeDefinition?> TargetDefinitionAsync(
        string referenceType, CancellationToken cancellationToken) =>
        ReferenceConditions.TargetDefinitionAsync(name => DefinitionAsync(name, cancellationToken), referenceType);

    /// <summary>
    /// The entry a reference points at, read once per request and at most
    /// <see cref="ReferenceConditions.MaxEntriesPerRequest"/> of them.
    /// </summary>
    private async Task<Models.Content?> ReferencedAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_referenced.TryGetValue(id, out var known))
            return known;

        if (_referenced.Count >= ReferenceConditions.MaxEntriesPerRequest)
        {
            if (!_warnedPastBound)
            {
                _warnedPastBound = true;
                logger?.LogWarning(
                    "A request followed references into more than {Bound} entries to answer permission checks. "
                  + "References past that are not read, so the conditions that follow them deny.",
                    ReferenceConditions.MaxEntriesPerRequest);
            }

            return null;
        }

        var target = await session.LoadAsync<Models.Content>(id, cancellationToken);
        _referenced[id] = target;
        return target;
    }

    private void WarnSecondHop(string contentType)
    {
        if (_warnedSecondHop)
            return;

        _warnedSecondHop = true;
        logger?.LogWarning(
            "A condition followed a reference into {ContentType}, whose own read rule follows another. "
          + "One reference is followed, so that rule grants nothing to the condition.",
            contentType);
    }
}
