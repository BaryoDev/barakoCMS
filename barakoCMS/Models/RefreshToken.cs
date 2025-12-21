namespace barakoCMS.Models;

/// <summary>
/// Represents a refresh token for JWT token rotation.
/// Refresh tokens have longer expiry (7 days) and are rotated on each use.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    
    /// <summary>
    /// The actual refresh token string (cryptographically secure random)
    /// </summary>
    public string Token { get; set; } = string.Empty;
    
    /// <summary>
    /// The user this refresh token belongs to
    /// </summary>
    public Guid UserId { get; set; }
    
    /// <summary>
    /// When this refresh token expires
    /// </summary>
    public DateTime ExpiresAt { get; set; }
    
    /// <summary>
    /// When this refresh token was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Whether this token has been revoked (used or invalidated)
    /// </summary>
    public bool IsRevoked { get; set; }
    
    /// <summary>
    /// Reason for revocation: "used", "logout", "password_change", "admin_revoke"
    /// </summary>
    public string? RevokedReason { get; set; }
    
    /// <summary>
    /// When this token was revoked (if applicable)
    /// </summary>
    public DateTime? RevokedAt { get; set; }
}
