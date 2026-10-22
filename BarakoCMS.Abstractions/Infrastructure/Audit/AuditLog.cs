using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Audit;

/// <summary>
/// Records one <see cref="AuditEvent"/> into a Marten session and chains it to the tenant's most
/// recent entry. The caller owns the transaction (call <c>SaveChangesAsync</c>) — same convention as
/// <c>PreviewToken</c> and Diagnostics'
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
        // An entry this session stored and has not saved yet is the newest, and a query cannot see
        // it. Chaining off the stored one instead would give two entries of one save the same parent.
        var previous = session.PendingChanges.AllChangedFor<AuditEvent>()
                           .Where(e => e.TenantSlug == tenantSlug)
                           .MaxBy(e => e.CreatedAt)
                       ?? await session.Query<AuditEvent>()
                           .Where(e => e.TenantSlug == tenantSlug)
                           .OrderByDescending(e => e.CreatedAt)
                           .FirstOrDefaultAsync(ct);

        var prevHash = previous?.Hash ?? AuditChain.GenesisHash;
        // Cut at the microsecond Postgres stores, so the stored text and the audit cursor agree exactly.
        var now = DateTime.UtcNow;
        var createdAt = new DateTime(now.Ticks - now.Ticks % 10, now.Kind);
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
