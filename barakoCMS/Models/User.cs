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
}
