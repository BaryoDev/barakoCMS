namespace barakoCMS.Models;

/// <summary>
/// A one-time passwordless sign-in code sent to a user's email. The code itself is never stored —
/// only a hash — and it is single-use with a short expiry and a per-code attempt cap.
/// </summary>
public class OtpCode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Recipient email, normalised to lowercase for lookup.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>BCrypt hash of the 6-digit code.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public int Attempts { get; set; }

    public bool Consumed { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
