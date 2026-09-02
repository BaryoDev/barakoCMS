using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Service for resolving user permissions using additive (union) role semantics: a user is
/// granted an action if ANY of their roles grants it. SuperAdmin bypasses all checks.
/// </summary>
public class PermissionResolver : IPermissionResolver
{
    private readonly IDocumentSession _session;
    private readonly IConditionEvaluator _conditionEvaluator;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public PermissionResolver(
        IDocumentSession session,
        IConditionEvaluator conditionEvaluator,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _conditionEvaluator = conditionEvaluator;
        _tenant = tenant;
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
        // Roles come from the user's membership in the current tenant (falling back to the user's
        // legacy roles when there's no membership).
        var roleIds = await barakoCMS.Infrastructure.Multitenancy.MembershipRoles
            .EffectiveRoleIdsAsync(_session, user, _tenant.Slug, cancellationToken);
        if (roleIds.Count == 0)
            return false;

        // Batch load all the user's roles in a SINGLE query (eliminates N+1)
        var roles = await _session.Query<Models.Role>()
            .Where(r => r.Id.In(roleIds))
            .ToListAsync(cancellationToken);

        if (roles.Count == 0)
            return false;

        // SUPER ADMIN BYPASS
        if (roles.Any(r => r.Name == "SuperAdmin"))
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
        foreach (var rule in rules)
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
                    _conditionEvaluator.Evaluate(rule.Conditions, content, user))
                {
                    return true; // Granted by at least one role
                }
            }
        }

        // None of the rules granted access
        return false;
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
        var user = await _session.LoadAsync<Models.User>(userId, cancellationToken);
        if (user is null)
            return false;

        var roleIds = await barakoCMS.Infrastructure.Multitenancy.MembershipRoles
            .EffectiveRoleIdsAsync(_session, user, _tenant.Slug, cancellationToken);
        if (roleIds.Count == 0)
            return false;

        var roles = await _session.Query<Models.Role>()
            .Where(r => r.Id.In(roleIds))
            .ToListAsync(cancellationToken);

        if (roles.Count == 0)
            return false;

        if (roles.Any(r => r.Name == "SuperAdmin"))
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
}
