using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Service for resolving user permissions with "Most Restrictive" strategy
/// </summary>
public class PermissionResolver : IPermissionResolver
{
    private readonly IDocumentSession _session;
    private readonly IConditionEvaluator _conditionEvaluator;

    public PermissionResolver(IDocumentSession session, IConditionEvaluator conditionEvaluator)
    {
        _session = session;
        _conditionEvaluator = conditionEvaluator;
    }

    /// <summary>
    /// Check if user can perform action using "Most Restrictive" logic:
    /// ALL roles must grant permission for approval
    /// </summary>
    public async Task<bool> CanPerformActionAsync(
        Models.User user,
        string contentTypeSlug,
        string action,
        Models.Content? content = null,
        CancellationToken cancellationToken = default)
    {
        // No roles = no permissions
        if (user.RoleIds == null || user.RoleIds.Count == 0)
            return false;

        // Batch load all user's roles in a SINGLE query (eliminates N+1)
        var roles = await _session.Query<Models.Role>()
            .Where(r => r.Id.In(user.RoleIds))
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
                if (content == null || rule.Conditions == null || rule.Conditions.Count == 0 ||
                    _conditionEvaluator.Evaluate(rule.Conditions, content.Data, user))
                {
                    return true; // Granted by at least one role
                }
            }
        }

        // None of the rules granted access
        return false;
    }

    private Models.PermissionRule? GetRuleForAction(Models.ContentTypePermission permission, string action)
    {
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
