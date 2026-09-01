using System.Security.Cryptography;
using System.Text;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// AES-GCM authenticated encryption with the wire format barakoCMS stores secrets in:
/// base64(nonce[12] | tag[16] | ciphertext).
/// </summary>
/// <remarks>
/// Extracted from <c>MfaSecretProtector</c> when email credentials needed the same treatment. The
/// format and the operations are byte for byte what that class did, deliberately: a second copy of
/// an encryption routine is two things to get right and one place to notice when only one of them
/// is fixed.
///
/// The key is passed in rather than derived here. Callers derive their own from their own
/// configuration, so rotating one secret's key does not make another secret undecryptable.
/// </remarks>
internal static class AesGcmEnvelope
{
    private const int NonceLen = 12; // AesGcm.NonceByteSizes.MaxSize
    private const int TagLen = 16;   // AesGcm.TagByteSizes.MaxSize

    /// <summary>Derives a 32 byte AES-256 key from arbitrary configured key material.</summary>
    internal static byte[] DeriveKey(string material) => SHA256.HashData(Encoding.UTF8.GetBytes(material));

    internal static string Protect(byte[] key, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLen];

        using var aes = new AesGcm(key, TagLen);
        aes.Encrypt(nonce, plain, cipher, tag);

        var outBytes = new byte[NonceLen + TagLen + cipher.Length];
        Buffer.BlockCopy(nonce, 0, outBytes, 0, NonceLen);
        Buffer.BlockCopy(tag, 0, outBytes, NonceLen, TagLen);
        Buffer.BlockCopy(cipher, 0, outBytes, NonceLen + TagLen, cipher.Length);
        return Convert.ToBase64String(outBytes);
    }

    internal static string Unprotect(byte[] key, string protectedValue)
    {
        var raw = Convert.FromBase64String(protectedValue);
        if (raw.Length < NonceLen + TagLen)
            throw new CryptographicException("Malformed protected value.");

        var nonce = raw.AsSpan(0, NonceLen);
        var tag = raw.AsSpan(NonceLen, TagLen);
        var cipher = raw.AsSpan(NonceLen + TagLen);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagLen);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
