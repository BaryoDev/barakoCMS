using System.Security.Cryptography;
using System.Text;
using barakoCMS.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Filters;

/// <summary>
/// The keys idempotency records are written with: one to seal stored response bodies, one to hash
/// requests. Both are derived for this purpose alone from the stored-secret key material
/// (<c>Secrets:Key</c>, falling back to <c>JWT:Key</c>), so neither is the key any other secret uses.
/// </summary>
/// <remarks>
/// A body is sealed with the record's scoped key as associated data. A database writer who copies
/// another record's body, or another secret's envelope, into a record gets nothing back: it does not
/// open in that context, and the retry is answered 409.
///
/// Requests are hashed with HMAC rather than plain SHA-256. A request body can hold a password or a
/// credential, and a plain hash in a database dump can be guessed against offline without ever
/// meeting the slow password hash that guards the same value elsewhere.
/// </remarks>
internal sealed class IdempotencyProtector
{
    private readonly byte[] _sealKey;
    private readonly byte[] _hashKey;

    public IdempotencyProtector(IConfiguration config)
    {
        var material = config["Secrets:Key"];
        if (string.IsNullOrEmpty(material)) material = config["JWT:Key"];
        if (string.IsNullOrEmpty(material))
            throw new InvalidOperationException("Secrets:Key or JWT:Key must be configured to protect idempotency records.");

        var root = AesGcmEnvelope.DeriveKey(material);
        _sealKey = HMACSHA256.HashData(root, "barakoCMS idempotency response body v1"u8);
        _hashKey = HMACSHA256.HashData(root, "barakoCMS idempotency request hash v1"u8);
    }

    public string Seal(byte[] body, string scopedKey) =>
        AesGcmEnvelope.Seal(_sealKey, body, Encoding.UTF8.GetBytes(scopedKey));

    /// <summary>The body, or null when it does not open for this record. Never throws.</summary>
    public byte[]? Open(string sealedBody, string scopedKey) =>
        AesGcmEnvelope.Open(_sealKey, sealedBody, Encoding.UTF8.GetBytes(scopedKey));

    public IncrementalHash CreateRequestHash() =>
        IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _hashKey);
}
