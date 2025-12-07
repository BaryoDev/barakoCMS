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

        // Load all user's roles
        var roles = new List<Models.Role>();
        foreach (var roleId in user.RoleIds)
        {
            var role = await _session.LoadAsync<Models.Role>(roleId, cancellationToken);
            if (role != null)
                roles.Add(role);
        }

        if (roles.Count == 0)
            return false;

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

        // MOST RESTRICTIVE: ALL rules must allow
        foreach (var rule in rules)
        {
            // 1. Check if rule is enabled
            if (!rule.Enabled)
                return false; // Any disabled rule denies access

            // 2. Check conditions (if present and content provided)
            if (content != null && rule.Conditions != null && rule.Conditions.Count > 0)
            {
                if (!_conditionEvaluator.Evaluate(rule.Conditions, content.Data, user))
                    return false; // Any failed condition denies access
            }
        }

        // All rules passed - ALLOW
        return true;
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
