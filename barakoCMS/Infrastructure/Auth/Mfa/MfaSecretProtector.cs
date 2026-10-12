using System.Security.Cryptography;
using System.Text;

namespace barakoCMS.Infrastructure.Auth.Mfa;

/// <summary>
/// Encrypts/decrypts a user's TOTP secret at rest with AES-GCM (authenticated encryption). The key is
/// derived (SHA-256) from <c>Mfa:Key</c> if set, otherwise from the JWT signing key — which startup
/// already guarantees is at least 32 chars — so a database dump alone does not yield working second
/// factors. Wire format: base64(nonce[12] | tag[16] | ciphertext).
/// </summary>
public interface IMfaSecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

public sealed class MfaSecretProtector : IMfaSecretProtector, barakoCMS.Infrastructure.Security.IKeyRingProtector
{
    private readonly barakoCMS.Infrastructure.Security.KeyRing _ring;

    // The derivation stays per class rather than shared. It reads Mfa:Key, and secrets already stored
    // were encrypted under it, so a shared derivation would silently retire every second factor.
    public MfaSecretProtector(IConfiguration config)
    {
        var material = config["Mfa:Key"];
        if (string.IsNullOrEmpty(material)) material = config["JWT:Key"];

        _ring = barakoCMS.Infrastructure.Security.KeyRing.FromConfiguration(config, "Mfa", material, prefixUnkeyedWrites: false);
        if (!_ring.CanProtect)
            throw new InvalidOperationException("Mfa:Keys, Mfa:Key or JWT:Key must be configured to protect MFA secrets.");
    }

    barakoCMS.Infrastructure.Security.KeyRing barakoCMS.Infrastructure.Security.IKeyRingProtector.Ring => _ring;

    public string Protect(string plaintext) => _ring.Protect(plaintext);

    public string Unprotect(string protectedValue) =>
        _ring.Unprotect(protectedValue)
        ?? throw new CryptographicException("The MFA secret does not decrypt under any key in the ring.");
}
