using Marten;

namespace BarakoCMS.Files;

/// <summary>
/// Removes a file for good: the record, its cached resizes, the bytes behind all of them, and an
/// audit entry saying who did it. Shared by the <c>Delete</c> route and the core's file seam so a
/// delete leaves the same things behind whichever way it was asked for.
/// </summary>
/// <remarks>
/// Whether the caller may delete the file is decided before this is called.
/// </remarks>
internal static class FileRemoval
{
    public static async Task RemoveAsync(
        IDocumentSession session,
        IFileStorage storage,
        string tenantSlug,
        StoredFile file,
        Guid actorUserId,
        string actorUsername,
        bool forced,
        string? ipAddress,
        CancellationToken ct)
    {
        // The resizes go with their original: they are reachable only through it, so a variant
        // outliving its parent would be bytes nothing can ever serve again.
        var variants = await session.Query<StoredFile>()
            .Where(v => v.ParentFileId == file.Id)
            .ToListAsync(ct);

        foreach (var variant in variants)
        {
            await storage.DeleteAsync(variant.StorageKey, ct);
            session.Delete(variant);
        }

        await storage.DeleteAsync(file.StorageKey, ct);
        session.Delete(file);

        await barakoCMS.Infrastructure.Audit.AuditLog.RecordAsync(
            session,
            tenantSlug,
            "file.deleted",
            actorUserId,
            actorUsername,
            targetType: "file",
            targetId: file.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["fileName"] = file.FileName,
                ["forced"] = forced,
                ["variants"] = variants.Count,
            },
            ipAddress: ipAddress,
            ct: ct);

        await session.SaveChangesAsync(ct);
    }
}
