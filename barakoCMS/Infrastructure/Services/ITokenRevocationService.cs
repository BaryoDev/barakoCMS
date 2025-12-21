namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Service for managing JWT token revocation (blacklisting).
/// Supports logout, password changes, and admin-initiated revocations.
/// </summary>
public interface ITokenRevocationService
{
    /// <summary>
    /// Revokes a specific JWT token by its JTI claim.
    /// </summary>
    /// <param name="jti">The JWT ID (jti claim) to revoke</param>
    /// <param name="userId">The user who owns the token</param>
    /// <param name="reason">Reason for revocation (e.g., "logout", "password_change")</param>
    /// <param name="expiry">When the original token expires (for cleanup)</param>
    /// <param name="ct">Cancellation token</param>
    Task RevokeTokenAsync(string jti, Guid userId, string reason, DateTime expiry, CancellationToken ct = default);
    
    /// <summary>
    /// Checks if a token has been revoked.
    /// Uses caching for performance.
    /// </summary>
    /// <param name="jti">The JWT ID to check</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the token is revoked, false otherwise</returns>
    Task<bool> IsTokenRevokedAsync(string jti, CancellationToken ct = default);
    
    /// <summary>
    /// Revokes all tokens for a specific user.
    /// Useful for password changes or admin actions.
    /// </summary>
    /// <param name="userId">The user whose tokens should be revoked</param>
    /// <param name="reason">Reason for revocation</param>
    /// <param name="ct">Cancellation token</param>
    Task RevokeAllUserTokensAsync(Guid userId, string reason, CancellationToken ct = default);
}
