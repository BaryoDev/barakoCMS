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
    private const int NonceLen = 12; // AesGcm.NonceByteSizes.MaxSize
    private const int TagLen = 16;   // AesGcm.TagByteSizes.MaxSize

    private readonly byte[] _key;

    public MfaSecretProtector(IConfiguration config)
    {
        var material = config["Mfa:Key"];
        if (string.IsNullOrEmpty(material)) material = config["JWT:Key"];
        if (string.IsNullOrEmpty(material))
            throw new InvalidOperationException("Mfa:Key or JWT:Key must be configured to protect MFA secrets.");
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(material)); // 32-byte AES-256 key
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLen];

        using var aes = new AesGcm(_key, TagLen);
        aes.Encrypt(nonce, plain, cipher, tag);

        var outBytes = new byte[NonceLen + TagLen + cipher.Length];
        Buffer.BlockCopy(nonce, 0, outBytes, 0, NonceLen);
        Buffer.BlockCopy(tag, 0, outBytes, NonceLen, TagLen);
        Buffer.BlockCopy(cipher, 0, outBytes, NonceLen + TagLen, cipher.Length);
        return Convert.ToBase64String(outBytes);
    }

    public string Unprotect(string protectedValue)
    {
        var raw = Convert.FromBase64String(protectedValue);
        if (raw.Length < NonceLen + TagLen)
            throw new CryptographicException("Malformed protected MFA secret.");

        var nonce = raw.AsSpan(0, NonceLen);
        var tag = raw.AsSpan(NonceLen, TagLen);
        var cipher = raw.AsSpan(NonceLen + TagLen);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagLen);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
