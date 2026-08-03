using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Audit;

/// <summary>
/// Records one <see cref="AuditEvent"/> into a Marten session and chains it to the tenant's most
/// recent entry. The caller owns the transaction (call <c>SaveChangesAsync</c>) — same convention as
/// <see cref="barakoCMS.Infrastructure.Preview.PreviewToken"/> and Diagnostics'
/// <c>ClientErrorRecorder</c>, so an audit record commits atomically with the action it describes.
///
/// Known limitation: the "previous entry" lookup and this entry's insert are not one atomic
/// operation, so two audit-worthy actions racing in the same tenant at the same instant could both
/// read the same previous hash and chain off it, producing two entries with the same
/// <see cref="AuditEvent.PrevHash"/>. That breaks the single-linked-list shape (detectable as a fork,
/// not silently accepted) but does not lose either entry. Real serialization would need a
/// per-tenant advisory lock, which this "S"-sized feature doesn't take on.
/// </summary>
public static class AuditLog
{
    public static async Task RecordAsync(
        IDocumentSession session,
        string tenantSlug,
        string action,
        Guid? actorUserId,
        string? actorUsername,
        string? targetType = null,
        string? targetId = null,
        Dictionary<string, object>? metadata = null,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        var previous = await session.Query<AuditEvent>()
            .Where(e => e.TenantSlug == tenantSlug)
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var prevHash = previous?.Hash ?? AuditChain.GenesisHash;
        var createdAt = DateTime.UtcNow;
        var hash = AuditChain.ComputeHash(prevHash, tenantSlug, action, actorUserId, targetType, targetId, createdAt);

        session.Store(new AuditEvent
        {
            TenantSlug = tenantSlug,
            Action = action,
            ActorUserId = actorUserId,
            ActorUsername = actorUsername,
            TargetType = targetType,
            TargetId = targetId,
            Metadata = metadata,
            IpAddress = ipAddress,
            CreatedAt = createdAt,
            PrevHash = prevHash,
            Hash = hash,
        });
    }
}
