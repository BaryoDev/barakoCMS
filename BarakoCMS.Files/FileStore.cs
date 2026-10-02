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
/// The members that take a caller apply <see cref="FileOwnership"/> to it, as the download and
/// delete routes do, after checking that it is a caller those routes would have let in at all:
/// signed in, not an API key, and signed in to this scope's tenant.
///
/// A save runs the checks the upload route runs and writes the record the upload route writes, so
/// the module's routes serve a file stored here as they serve an upload.
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
    };

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

        // The public download route redirects to the store's own URL on the same condition, and
        // serves every other public file itself.
        return UploadTypes.IsExactly(file.ContentType) && !string.IsNullOrEmpty(file.PublicUrl)
            ? file.PublicUrl
            : $"/api/public/files/{file.Id}";
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

        var contentType = UploadTypes.Allowed(file.ContentType);
        if (contentType is null)
        {
            return Refuse($"Only these types are allowed: {string.Join(", ", UploadTypes.Names)}.");
        }

        var bytes = await ReadUpToAsync(file.Content, UploadTypes.MaxBytes, cancellationToken);
        if (bytes is null)
        {
            return Refuse($"File is too large (max {UploadTypes.MaxBytes / (1024 * 1024)} MB).");
        }

        if (bytes.Length == 0)
        {
            return Refuse("A file is required.");
        }

        if (!UploadTypes.Matches(contentType, bytes.AsSpan(0, Math.Min(bytes.Length, UploadTypes.HeadLength))))
        {
            return Refuse($"The file's content is not {contentType}.");
        }

        var fileName = Path.GetFileName(file.FileName ?? string.Empty);

        if (scanner.Configured)
        {
            ScanResult scan;
            using (var forScanning = new MemoryStream(bytes, writable: false))
            {
                scan = await scanner.ScanAsync(forScanning, cancellationToken);
            }

            if (scan.Verdict != ScanVerdict.Clean)
            {
                await RecordRefusalAsync(fileName, contentType, bytes.Length, file.Owner, scan, cancellationToken);

                // An unreachable scanner is not evidence that a file is safe, so it is refused as
                // an infected one is. Only the scanner's silence can change on a later attempt.
                return scan.Verdict == ScanVerdict.Infected
                    ? Refuse($"This file was refused by the virus scanner ({scan.Signature}).")
                    : Refuse("This file could not be scanned, so it was not stored. Try again shortly.", canRetry: true);
            }
        }

        var key = FileKeys.ForUpload(file.IsPublic, UploadTypes.Extension(contentType));

        StoredObjectRef stored;
        using (var content = new MemoryStream(bytes, writable: false))
        {
            stored = await storage.PutAsync(content, key, contentType, file.IsPublic, cancellationToken);
        }

        var record = new StoredFile
        {
            FileName = fileName,
            ContentType = contentType,
            Size = bytes.Length,
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

        // The delete route asks for the capability first and for ownership second. A caller missing
        // either is told the file is not there, so this cannot be used to learn which ids exist.
        if (!IsSignedInHere(caller) || !await HoldsUploadFilesAsync(caller, cancellationToken))
        {
            return FileDeleteResult.NotFound;
        }

        var file = await session.LoadAsync<StoredFile>(id, cancellationToken);
        if (file is null || file.ParentFileId is not null || !FileOwnership.CanAccess(caller, file))
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

        return file.IsPublic || (IsSignedInHere(caller) && FileOwnership.CanAccess(caller, file)) ? file : null;
    }

    /// <summary>
    /// Whether the authenticated routes of this module would have let <paramref name="caller"/> in
    /// before asking about any one file.
    /// </summary>
    /// <remarks>
    /// Three things the request pipeline settles before a route runs, and that nothing settles for
    /// a principal handed to a method. An API key reaches no file route at all. A token names the
    /// tenant it was issued for, its role claims are that tenant's, and a request carrying it into
    /// another tenant is refused; the comparison is the one that refusal makes.
    /// </remarks>
    private bool IsSignedInHere(ClaimsPrincipal caller)
    {
        if (caller.Identity is not { IsAuthenticated: true } || caller.HasClaim("auth_method", "apikey"))
        {
            return false;
        }

        var claimed = caller.FindFirst("tenant")?.Value;
        return string.IsNullOrEmpty(claimed) || string.Equals(claimed, tenant.Slug, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The capability gate on the delete route, asked of a principal instead of a request.</summary>
    private async Task<bool> HoldsUploadFilesAsync(ClaimsPrincipal caller, CancellationToken ct)
    {
        if (FileCapabilities.LegacyRoles.Any(caller.IsInRole)
            && configuration.GetValue(CapabilityGateProcessor.LegacyRoleFallbackKey, false))
        {
            return true;
        }

        return Guid.TryParse(caller.FindFirst("UserId")?.Value, out var userId)
            && await permissions.HasCapabilityAsync(userId, FileCapabilities.UploadFiles, ct);
    }

    /// <summary>
    /// The audit entry the upload route writes for a file the scanner refused, committed on its own
    /// because nothing else is stored for a refused file.
    /// </summary>
    private async Task RecordRefusalAsync(
        string fileName, string contentType, long size, Guid owner, ScanResult scan, CancellationToken ct)
    {
        await barakoCMS.Infrastructure.Audit.AuditLog.RecordAsync(
            session,
            tenant.Slug,
            scan.Verdict == ScanVerdict.Infected ? "file.refused.infected" : "file.refused.unscanned",
            owner == Guid.Empty ? null : (Guid?)owner,
            string.Empty,
            targetType: "file",
            targetId: fileName,
            metadata: new Dictionary<string, object>
            {
                ["fileName"] = fileName,
                ["contentType"] = contentType,
                ["size"] = size,
                ["reason"] = scan.Signature ?? scan.Error ?? "unknown",
            },
            ct: ct);

        await session.SaveChangesAsync(ct);
    }

    /// <summary>The stream's remaining bytes, or null when there are more than <paramref name="limit"/>.</summary>
    private static async Task<byte[]?> ReadUpToAsync(Stream stream, long limit, CancellationToken ct)
    {
        using var held = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (held.Length + read > limit)
            {
                return null;
            }

            held.Write(chunk, 0, read);
        }

        return held.ToArray();
    }
}
