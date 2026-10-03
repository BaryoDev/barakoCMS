using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using Marten;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Files;

/// <summary>
/// The core's way to store, read and delete a file without referencing this module.
/// </summary>
/// <remarks>
/// Every member answers the question one of this module's routes answers, and nothing more:
///
/// The public members hand a file over only when it is marked public, as the public download route
/// does. They take no caller, so no private file leaves through them.
///
/// The members that take a caller apply <see cref="FileAccessRule"/> to it, as the download and
/// delete routes do. It starts with three checks a principal handed to a method has not had: signed
/// in, not an API key, and not a token for another tenant. The caller has to be the current request's.
/// Whether its token was revoked, its tenant switched off or its device refused is settled by the
/// request pipeline and is not asked again here.
///
/// A save runs the checks the upload route runs and writes the record the upload route writes, so
/// the module's routes serve a file stored here as they serve an upload.
///
/// A save and a delete commit through the scope's session, which the storage shares, so both
/// refuse to start while that session holds work the caller staged. What they commit is then
/// their own and nothing else.
///
/// The record and the bytes go through the scope's own session and storage, which are opened for
/// the scope's tenant, so an id of another tenant loads nothing. A cached resize is absent here for
/// the reason both download routes refuse one: it has no access rules of its own.
/// </remarks>
internal sealed class FileStore(
    IDocumentSession session,
    IFileStorage storage,
    IFileScanner scanner,
    IPermissionResolver permissions,
    IConfiguration configuration,
    TenantContext tenant) : IFileStore
{
    public async Task<StoredFileInfo?> FindPublicAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var file = await LoadPublicAsync(id, cancellationToken);
        return file is null ? null : Info(file);
    }

    private static StoredFileInfo Info(StoredFile file) => new()
    {
        Id = file.Id,
        FileName = file.FileName,
        // A row stored before uploads were checked carries whatever type its client declared.
        ContentType = UploadTypes.IsExactly(file.ContentType) ? file.ContentType : "application/octet-stream",
        Size = file.Size,
        PublicUrl = file.IsPublic ? PublicAddress(file) : null,
        Alt = file.Alt,
        Caption = file.Caption,
    };

    // The public download route redirects to the store's own URL on the same condition, and
    // serves every other public file itself.
    private static string PublicAddress(StoredFile file) =>
        UploadTypes.IsExactly(file.ContentType) && !string.IsNullOrEmpty(file.PublicUrl)
            ? file.PublicUrl
            : $"/api/public/files/{file.Id}";

    public async Task<IReadOnlyDictionary<Guid, StoredFileInfo>> FindPublicManyAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        var files = await LoadManyAsync(ids, cancellationToken);
        return files.Where(f => f.IsPublic).ToDictionary(f => f.Id, Info);
    }

    public async Task<IReadOnlyDictionary<Guid, StoredFileInfo>> FindManyAsync(
        IReadOnlyCollection<Guid> ids, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var found = new Dictionary<Guid, StoredFileInfo>();
        foreach (var file in await LoadManyAsync(ids, cancellationToken))
        {
            if (await MayReadAsync(file, caller, cancellationToken))
            {
                found[file.Id] = Info(file);
            }
        }

        return found;
    }

    /// <summary>The records among these ids in one read, without the cached resizes.</summary>
    private async Task<IReadOnlyList<StoredFile>> LoadManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var wanted = ids.Distinct().ToArray();
        if (wanted.Length == 0)
        {
            return [];
        }

        var files = await session.Query<StoredFile>().Where(f => f.Id.In(wanted)).ToListAsync(ct);
        return files.Where(f => f.ParentFileId is null).ToList();
    }

    public async Task<Stream?> OpenPublicAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var file = await LoadPublicAsync(id, cancellationToken);
        if (file is null)
        {
            return null;
        }

        // The storage hands back the whole object as one array, so the array is exposed and a
        // caller that wants the bytes does not copy them out of the stream.
        var bytes = await storage.GetAsync(file.StorageKey, cancellationToken);
        return bytes is null ? null : new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
    }

    private async Task<StoredFile?> LoadPublicAsync(Guid id, CancellationToken ct)
    {
        var file = await session.LoadAsync<StoredFile>(id, ct);
        return file is null || !file.IsPublic || file.ParentFileId is not null ? null : file;
    }

    public async Task<string?> PublicUrlAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var file = await LoadPublicAsync(id, cancellationToken);
        if (file is null)
        {
            return null;
        }

        return PublicAddress(file);
    }

    public async Task<StoredFileInfo?> FindAsync(Guid id, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        var file = await LoadForAsync(id, caller, cancellationToken);
        return file is null ? null : Info(file);
    }

    public async Task<Stream?> OpenAsync(Guid id, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        var file = await LoadForAsync(id, caller, cancellationToken);
        if (file is null)
        {
            return null;
        }

        var bytes = await storage.GetAsync(file.StorageKey, cancellationToken);
        return bytes is null ? null : new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
    }

    public async Task<FileSaveResult> SaveAsync(FileToStore file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        RefuseStagedWork(nameof(SaveAsync));

        var contentType = UploadTypes.Allowed(file.ContentType);
        if (contentType is null)
        {
            return Refuse($"Only these types are allowed: {string.Join(", ", UploadTypes.Names)}.");
        }

        using var held = await ReadUpToAsync(file.Content, UploadTypes.MaxBytes, cancellationToken);
        if (held is null)
        {
            return Refuse($"File is too large (max {UploadTypes.MaxBytes / (1024 * 1024)} MB).");
        }

        // One copy of the file, read in place for the check, the scan and the store.
        var size = (int)held.Length;
        var buffer = held.GetBuffer();

        if (size == 0)
        {
            return Refuse("A file is required.");
        }

        if (!UploadTypes.Matches(contentType, buffer.AsSpan(0, Math.Min(size, UploadTypes.HeadLength))))
        {
            return Refuse($"The file's content is not {contentType}.");
        }

        var fileName = Path.GetFileName(file.FileName ?? string.Empty);

        if (scanner.Configured)
        {
            ScanResult scan;
            using (var forScanning = new MemoryStream(buffer, 0, size, writable: false))
            {
                scan = await scanner.ScanAsync(forScanning, cancellationToken);
            }

            if (scan.Verdict != ScanVerdict.Clean)
            {
                await RecordRefusalAsync(fileName, contentType, size, file, scan, cancellationToken);

                // An unreachable scanner is not evidence that a file is safe, so it is refused as
                // an infected one is. Only the scanner's silence can change on a later attempt.
                return scan.Verdict == ScanVerdict.Infected
                    ? Refuse($"This file was refused by the virus scanner ({scan.Signature}).")
                    : Refuse("This file could not be scanned, so it was not stored. Try again shortly.", canRetry: true);
            }
        }

        var key = FileKeys.ForUpload(file.IsPublic, UploadTypes.Extension(contentType));

        StoredObjectRef stored;
        using (var content = new MemoryStream(buffer, 0, size, writable: false))
        {
            stored = await storage.PutAsync(content, key, contentType, file.IsPublic, cancellationToken);
        }

        var record = new StoredFile
        {
            FileName = fileName,
            ContentType = contentType,
            Size = size,
            Provider = storage.Provider,
            StorageKey = stored.Key,
            IsPublic = file.IsPublic,
            PublicUrl = stored.PublicUrl,
            UploadedBy = file.Owner,
        };
        session.Store(record);
        await session.SaveChangesAsync(cancellationToken);

        return new FileSaveResult { File = Info(record) };
    }

    public async Task<FileDeleteResult> DeleteAsync(
        Guid id, ClaimsPrincipal caller, bool force = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        RefuseStagedWork(nameof(DeleteAsync));

        // The delete route asks for the capability first and for ownership second. A caller missing
        // either is told the file is not there, so this cannot be used to learn which ids exist.
        if (!IsSignedInHere(caller) || !await HoldsUploadFilesAsync(caller, cancellationToken))
        {
            return FileDeleteResult.NotFound;
        }

        var file = await session.LoadAsync<StoredFile>(id, cancellationToken);
        if (file is null || file.ParentFileId is not null
            || !await FileAccessRule.MayAccessAsync(caller, file, tenant.Slug, permissions, configuration, cancellationToken))
        {
            return FileDeleteResult.NotFound;
        }

        if (!force && await FileUsage.Referencing(session, file).CountAsync(cancellationToken) > 0)
        {
            return FileDeleteResult.InUse;
        }

        Guid.TryParse(caller.FindFirst("UserId")?.Value, out var userId);

        await FileRemoval.RemoveAsync(
            session,
            storage,
            tenant.Slug,
            file,
            userId,
            caller.FindFirst("Username")?.Value ?? string.Empty,
            force,
            ipAddress: null,
            cancellationToken);

        return FileDeleteResult.Deleted;
    }

    private static FileSaveResult Refuse(string reason, bool canRetry = false) =>
        new() { Refused = reason, CanRetry = canRetry };

    /// <summary>
    /// The file when <paramref name="caller"/> could download it through one of the two download
    /// routes: any public file, or a private one the authenticated route would hand this caller.
    /// </summary>
    private async Task<StoredFile?> LoadForAsync(Guid id, ClaimsPrincipal caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var file = await session.LoadAsync<StoredFile>(id, ct);
        if (file is null || file.ParentFileId is not null)
        {
            return null;
        }

        return await MayReadAsync(file, caller, ct) ? file : null;
    }

    /// <summary>
    /// Whether <paramref name="caller"/> may download this file, which is not a cached resize. The
    /// one rule for reading a file for a caller, one id or many.
    /// </summary>
    private async Task<bool> MayReadAsync(StoredFile file, ClaimsPrincipal caller, CancellationToken ct) =>
        file.IsPublic || await FileAccessRule.MayAccessAsync(caller, file, tenant.Slug, permissions, configuration, ct);

    /// <summary><see cref="FileAccessRule.IsSignedInHere"/>, for the scope's tenant.</summary>
    private bool IsSignedInHere(ClaimsPrincipal caller) => FileAccessRule.IsSignedInHere(caller, tenant.Slug);

    /// <summary>The capability gate on the delete route, asked of a principal instead of a request.</summary>
    private async Task<bool> HoldsUploadFilesAsync(ClaimsPrincipal caller, CancellationToken ct)
    {
        if (FileCapabilities.Defaults.LegacyRoles.Any(caller.IsInRole)
            && configuration.GetValue(CapabilityGateProcessor.LegacyRoleFallbackKey, false))
        {
            return true;
        }

        return Guid.TryParse(caller.FindFirst("UserId")?.Value, out var userId)
            && await permissions.HasCapabilityAsync(userId, FileCapabilities.UploadFiles, ct);
    }

    /// <summary>
    /// The audit entry for a file the scanner refused, under the action the upload route uses.
    /// </summary>
    /// <remarks>
    /// The route names the signed-in user, their name and their address. Here the actor is whoever
    /// the caller said supplied the file, which is nobody for a file a job produced. The owner is
    /// who the file would have belonged to, not who sent it, so it goes in the metadata. Committed
    /// on its own: the session held nothing when the save started.
    /// </remarks>
    private async Task RecordRefusalAsync(
        string fileName, string contentType, long size, FileToStore file, ScanResult scan, CancellationToken ct)
    {
        var metadata = new Dictionary<string, object>
        {
            ["fileName"] = fileName,
            ["contentType"] = contentType,
            ["size"] = size,
            ["reason"] = scan.Signature ?? scan.Error ?? "unknown",
        };

        if (file.Owner != Guid.Empty)
        {
            metadata["owner"] = file.Owner.ToString();
        }

        await barakoCMS.Infrastructure.Audit.AuditLog.RecordAsync(
            session,
            tenant.Slug,
            scan.Verdict == ScanVerdict.Infected ? "file.refused.infected" : "file.refused.unscanned",
            file.SuppliedBy,
            actorUsername: null,
            targetType: "file",
            targetId: fileName,
            metadata: metadata,
            ct: ct);

        await session.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Stops a save or a delete that would commit work the caller staged on the scope's session.
    /// </summary>
    /// <remarks>
    /// The storage and the audit entry commit through that session, some of them before the file's
    /// own record and some on a path that stores no file at all. With work already staged, a
    /// refused or failed save would commit the caller's rows with no file behind them.
    /// </remarks>
    private void RefuseStagedWork(string member)
    {
        var pending = session.PendingChanges;
        if (pending.Operations().Any() || pending.Streams().Any())
        {
            throw new InvalidOperationException(
                $"IFileStore.{member} commits the scope's session, and that session holds changes that are not saved yet. "
                + "Call it before staging anything else, or save those changes first.");
        }
    }

    /// <summary>
    /// The stream's remaining bytes, or null when there are more than <paramref name="limit"/>.
    /// The caller disposes the result.
    /// </summary>
    private static async Task<MemoryStream?> ReadUpToAsync(Stream stream, long limit, CancellationToken ct)
    {
        var capacity = 0;
        if (stream.CanSeek)
        {
            // Known up front: refuse without reading, and size the one buffer to fit.
            var remaining = stream.Length - stream.Position;
            if (remaining > limit)
            {
                return null;
            }

            capacity = (int)Math.Max(remaining, 0);
        }

        var held = new MemoryStream(capacity);
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (held.Length + read > limit)
            {
                held.Dispose();
                return null;
            }

            held.Write(chunk, 0, read);
        }

        return held;
    }
}
