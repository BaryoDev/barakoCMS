namespace barakoCMS.Models;

/// <summary>
/// A user's TOTP (authenticator-app) second factor. Global (SingleTenanted), keyed by the user's Id.
/// The shared secret is stored encrypted at rest; recovery codes are stored only as BCrypt hashes and
/// are single-use. The row is pending until the first correct code confirms enrollment (Enabled = true),
/// at which point login for that user requires a second factor.
/// </summary>
public class MfaSecret
{
    public Guid Id { get; set; } // == User.Id

    /// <summary>AES-GCM ciphertext of the base32 TOTP secret. Never store or return the raw secret.</summary>
    public string EncryptedSecret { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>BCrypt hashes of one-time recovery codes; an entry is removed when its code is used.</summary>
    public List<string> RecoveryCodeHashes { get; set; } = new();

    /// <summary>
    /// The last TOTP time-step consumed. A code is rejected if its step is not strictly greater, so a
    /// captured code cannot be replayed within its validity window.
    /// </summary>
    public long LastUsedTimeStep { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ConfirmedAt { get; set; }
}
