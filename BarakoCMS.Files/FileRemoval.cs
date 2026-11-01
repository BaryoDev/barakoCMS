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
        BarakoCMS.Files.Storage.FileStorageResolver storage,
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

        // Resolved before anything is staged, so a file whose storage cannot be built stops the
        // removal with the record and the bytes both still in place.
        var byteDeletes = new List<(BarakoCMS.Files.IFileStorage Storage, string Key)>();
        foreach (var variant in variants)
        {
            byteDeletes.Add((await storage.ForRemovalAsync(variant, ct), variant.StorageKey));
            session.Delete(variant);
        }

        byteDeletes.Add((await storage.ForRemovalAsync(file, ct), file.StorageKey));
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

        // After the save: if it fails, the bytes are still there for the record that stayed.
        await DeleteBytesAsync(byteDeletes, ct);
    }

    /// <summary>Every delete is tried, so one failing resize does not strand the original's bytes.</summary>
    private static async Task DeleteBytesAsync(
        List<(BarakoCMS.Files.IFileStorage Storage, string Key)> byteDeletes,
        CancellationToken ct)
    {
        var failures = new List<Exception>();
        foreach (var (target, key) in byteDeletes)
        {
            try
            {
                await target.DeleteAsync(key, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures.Add(e);
            }
        }

        if (failures.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }
    }
}
