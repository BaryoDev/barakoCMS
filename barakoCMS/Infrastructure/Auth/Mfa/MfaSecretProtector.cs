using System.Security.Cryptography;
using System.Text;

namespace barakoCMS.Infrastructure.Auth.Mfa;

/// <summary>
/// Encrypts/decrypts a user's TOTP secret at rest with AES-GCM (authenticated encryption), so a
/// database dump alone does not yield working second factors. With a key ring (<c>Mfa:Keys:{id}</c>
/// and <c>Mfa:CurrentKey</c>) a value is written as <c>enc:v1:{id}:{envelope}</c> under the current
/// key. Without one it is written as the bare envelope, base64(nonce[12] | tag[16] | ciphertext),
/// under a key derived from <c>Mfa:Key</c> or else the JWT signing key; once a ring exists that
/// single key only reads values written before it.
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
