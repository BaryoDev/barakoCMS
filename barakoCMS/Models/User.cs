namespace barakoCMS.Models;

public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public List<Guid> RoleIds { get; set; } = new();
    public List<Guid> GroupIds { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Number of consecutive failed login attempts
    /// </summary>
    public int FailedLoginAttempts { get; set; }
    
    /// <summary>
    /// When the account lockout expires (null if not locked)
    /// </summary>
    public DateTime? LockoutUntil { get; set; }

    /// <summary>
    /// Access tokens issued before this instant are refused. Null means no security event has ever
    /// happened to this account, which is the common case and skips the check entirely.
    /// </summary>
    /// <remarks>
    /// Revoking refresh tokens stops a session being renewed and does nothing to an access token
    /// already issued, which stays valid for up to fifteen minutes. So enabling MFA, changing a
    /// password or having an administrator reset one all left a stolen session working for the rest
    /// of that window.
    ///
    /// Bumped by <c>RevokeRefreshTokens.ForUserAsync</c>, so it moves wherever sessions are already
    /// being invalidated rather than at three call sites that have to remember.
    /// </remarks>
    public DateTime? TokensValidFrom { get; set; }
}
