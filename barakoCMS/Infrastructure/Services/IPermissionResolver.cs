namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Service for evaluating user permissions
/// </summary>
public interface IPermissionResolver
{
    /// <summary>
    /// Check if a user can perform an action on a content type
    /// </summary>
    /// <param name="user">The user to check permissions for</param>
    /// <param name="contentTypeSlug">The content type slug (e.g., "article", "product")</param>
    /// <param name="action">The action to perform ("create", "read", "update", "delete")</param>
    /// <param name="content">Optional content instance for condition evaluation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the user can perform the action, false otherwise</returns>
    Task<bool> CanPerformActionAsync(
        Models.User user,
        string contentTypeSlug,
        string action,
        Models.Content? content = null,
        CancellationToken cancellationToken = default);
}
