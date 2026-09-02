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

    /// <summary>
    /// Whether a user holds a system-wide capability, resolved from the roles they hold in the
    /// current tenant. Takes an id rather than the document so a cached answer costs no query at all.
    /// </summary>
    /// <param name="userId">The caller, as named by the request's <c>UserId</c> claim.</param>
    /// <param name="capability">A value from <see cref="Models.SystemCapabilities"/>.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if any of the user's roles grants the capability, false otherwise.</returns>
    Task<bool> HasCapabilityAsync(
        Guid userId,
        string capability,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evict any cached permission decisions for a single user. Call after that user's role
    /// assignments change so revoked access takes effect immediately instead of after the TTL.
    /// </summary>
    void InvalidateUserPermissions(Guid userId);

    /// <summary>
    /// Evict all cached permission decisions. Call after a role's permissions change, which can
    /// affect every user holding that role.
    /// </summary>
    void InvalidateAllPermissions();
}
