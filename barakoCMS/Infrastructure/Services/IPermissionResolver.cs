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
    /// A SQL predicate that selects the rows this user may read of a content type, when the rules
    /// can be expressed as one.
    /// </summary>
    /// <remarks>
    /// A default returning <see cref="ReadPredicate.None"/>, so a module with its own resolver keeps
    /// compiling and keeps behaving exactly as it did: None means "no predicate", and every caller
    /// answers that by evaluating per item the way it always has. This is an optimisation with a
    /// correct fallback, not a second authorisation path, and a resolver that never implements it is
    /// not less safe for that.
    ///
    /// Nothing is granted by the predicate that
    /// <see cref="CanPerformActionAsync(Models.User, string, string, Models.Content?, CancellationToken)"/>
    /// would not grant. That is the property <c>PermissionPredicateAgreementTests</c> exists to hold.
    /// </remarks>
    Task<ReadPredicate> ReadPredicateAsync(
        Models.User user,
        string contentTypeSlug,
        CancellationToken cancellationToken = default)
        => Task.FromResult(ReadPredicate.None);

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
