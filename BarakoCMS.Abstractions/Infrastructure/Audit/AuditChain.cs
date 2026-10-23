using System.Security.Cryptography;
using System.Text;

namespace barakoCMS.Infrastructure.Audit;

/// <summary>
/// Pure hash-chain math for <see cref="Models.AuditEvent"/>, kept separate from the recording/storage
/// side (<see cref="AuditLog"/>) so the chain computation itself is unit-testable without a database.
/// </summary>
public static class AuditChain
{
    /// <summary>The <see cref="Models.AuditEvent.PrevHash"/> value for the first entry in a tenant's
    /// chain — a hex string the same length as a real hash, so every stored value is shaped alike.</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// SHA-256 (hex, lowercase) over the previous entry's hash and this entry's own fields. Any
    /// change to a past entry's fields, or to its recorded hash, changes every hash computed after it.
    /// </summary>
    public static string ComputeHash(
        string prevHash,
        string tenantSlug,
        string action,
        Guid? actorUserId,
        string? targetType,
        string? targetId,
        DateTime createdAt)
    {
        var canonical = string.Join('|',
            prevHash,
            tenantSlug,
            action,
            actorUserId?.ToString() ?? string.Empty,
            targetType ?? string.Empty,
            targetId ?? string.Empty,
            createdAt.ToString("O"));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
