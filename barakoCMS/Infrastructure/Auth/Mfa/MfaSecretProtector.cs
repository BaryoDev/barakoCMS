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

public sealed class MfaSecretProtector : IMfaSecretProtector
{
    private readonly byte[] _key;

    public MfaSecretProtector(IConfiguration config)
    {
        var material = config["Mfa:Key"];
        if (string.IsNullOrEmpty(material)) material = config["JWT:Key"];
        if (string.IsNullOrEmpty(material))
            throw new InvalidOperationException("Mfa:Key or JWT:Key must be configured to protect MFA secrets.");
        _key = barakoCMS.Infrastructure.Security.AesGcmEnvelope.DeriveKey(material); // 32-byte AES-256 key
    }

    // The key derivation above stays here rather than moving with the cipher. It reads Mfa:Key, and
    // secrets already stored were encrypted under it, so a shared derivation would silently retire
    // every second factor in the database.
    public string Protect(string plaintext) =>
        barakoCMS.Infrastructure.Security.AesGcmEnvelope.Protect(_key, plaintext);

    public string Unprotect(string protectedValue) =>
        barakoCMS.Infrastructure.Security.AesGcmEnvelope.Unprotect(_key, protectedValue);
}
