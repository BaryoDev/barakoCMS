namespace barakoCMS.Models;

/// <summary>
/// Represents a refresh token for JWT token rotation.
/// Refresh tokens have longer expiry (7 days) and are rotated on each use.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    
    /// <summary>
    /// The token in plain text. Only rows written before tokens were hashed have it; new rows leave
    /// it null and set <see cref="TokenHash"/>. Kept so those rows still refresh once, which
    /// replaces them with a hashed row.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// SHA-256 of the token, from <see cref="HashOf"/>. What a presented token is looked up by, so
    /// a copy of this table does not hold working sessions.
    /// </summary>
    public string? TokenHash { get; set; }

    /// <summary>The value stored in <see cref="TokenHash"/> for a token: SHA-256, lowercase hex.</summary>
    public static string HashOf(string token) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    
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

    /// <summary>
    /// The client device id (X-Device-Id) this token is bound to, when device trust is enabled.
    /// Lets a refresh be tied to its device and revoked with it.
    /// </summary>
    public string? DeviceId { get; set; }
}
