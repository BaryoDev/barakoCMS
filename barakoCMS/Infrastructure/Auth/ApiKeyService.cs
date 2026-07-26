using System.Security.Cryptography;
using System.Text;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Mints and hashes API-key secrets. A secret is <c>bcms_</c> + 32 random bytes (URL-safe base64),
/// giving 256 bits of entropy. Only the SHA-256 hash is persisted; the plaintext is returned once and
/// never again. Lookup at auth time hashes the presented secret and matches on the (unique-indexed)
/// hash column — a 256-bit hash is not a meaningful timing oracle, so an indexed equality lookup is
/// safe, and we never compare raw secrets.
/// </summary>
public sealed class ApiKeyService
{
    public const string Prefix = "bcms_";

    /// <summary>The generated secret, its display prefix, and the hash to store.</summary>
    public readonly record struct GeneratedKey(string Secret, string DisplayPrefix, string Hash);

    public GeneratedKey Generate()
    {
        var random = Base64UrlNoPadding(RandomNumberGenerator.GetBytes(32));
        var secret = Prefix + random;
        // Enough of the front to identify the key in a list, never enough to use it.
        var displayPrefix = Prefix + random[..8];
        return new GeneratedKey(secret, displayPrefix, Hash(secret));
    }

    /// <summary>SHA-256 of the secret as uppercase hex. Deterministic — the same secret always hashes
    /// to the same value, which is what makes the indexed lookup work.</summary>
    public static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Does a presented token even look like one of ours? Cheap pre-check before hashing.</summary>
    public static bool LooksLikeApiKey(string? token) =>
        token is not null && token.StartsWith(Prefix, StringComparison.Ordinal);

    private static string Base64UrlNoPadding(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
