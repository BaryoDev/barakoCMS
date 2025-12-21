namespace barakoCMS.Models;

/// <summary>
/// Represents a revoked JWT access token.
/// Used for token blacklisting to support logout and forced invalidation.
/// </summary>
public class RevokedToken
{
    public Guid Id { get; set; }
    
    /// <summary>
    /// The JWT ID (jti claim) of the revoked token
    /// </summary>
    public string TokenJti { get; set; } = string.Empty;
    
    /// <summary>
    /// The user who owned this token
    /// </summary>
    public Guid UserId { get; set; }
    
    /// <summary>
    /// When this token was revoked
    /// </summary>
    public DateTime RevokedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When the original token expires (for automatic cleanup)
    /// </summary>
    public DateTime ExpiresAt { get; set; }
    
    /// <summary>
    /// Reason for revocation: "logout", "password_change", "admin_revoke"
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}
