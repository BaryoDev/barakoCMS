namespace barakoCMS.Models;

/// <summary>
/// A self-registration that has been asked for but not yet proven. It becomes a <see cref="User"/>
/// only when the address it names hands back the token that was emailed to it.
/// </summary>
/// <remarks>
/// The reason this is a document of its own rather than a flag on <see cref="User"/> is the join
/// key. External sign-in matches a provider's verified email to a local account by address and
/// nothing else, so any row holding an address nobody proved is a landing pad: register as
/// somebody else's address, wait for them to sign in with Google, and the provider hands them your
/// account. Keeping the unproven registration out of the users table means there is no row to land
/// on. See DECISIONS.md D10.
///
/// Nothing here is unique-indexed. Two people may hold pending registrations for the same username
/// or address at once; the first to verify becomes the user and the other is refused at verify
/// time. Reserving the name before anybody proved the address would hand an attacker a way to hold
/// usernames without ever owning a mailbox.
/// </remarks>
public class PendingRegistration
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Username { get; set; } = string.Empty;

    /// <summary>The address the token was emailed to, normalised to lowercase for lookup.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>BCrypt hash of the chosen password. The plaintext is never stored, exactly as on <see cref="User"/>.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>BCrypt hash of the token's secret half, like <see cref="OtpCode.CodeHash"/>.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public bool Consumed { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
