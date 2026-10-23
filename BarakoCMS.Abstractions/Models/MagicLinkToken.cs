namespace barakoCMS.Models;

/// <summary>
/// A single-use sign-in link sent to a user's email. Only a hash of the secret half is stored.
/// </summary>
public class MagicLinkToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Recipient email, normalised to lowercase for lookup.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>The tenant the link was requested in. It signs in to that tenant only.</summary>
    public string TenantSlug { get; set; } = string.Empty;

    /// <summary>BCrypt hash of the secret half of the token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public bool Consumed { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
