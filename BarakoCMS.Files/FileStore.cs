using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using Marten;

namespace BarakoCMS.Files;

/// <summary>
/// The core's way to read a stored file without referencing this module.
/// </summary>
/// <remarks>
/// Both the record and the bytes are read through the scope's own session and storage, which are
/// opened for the scope's tenant, so an id of another tenant loads nothing. A cached resize is
/// absent here for the reason both download routes refuse one: it has no access rules of its own.
///
/// Who may read is this module's decision and stays here. A public file is anybody's, as on the
/// public download route. Any other file is handed over only for a user
/// <see cref="FileOwnership"/> would let download it. The caller is told nothing about a file it is
/// not given, including whether there is one.
/// </remarks>
internal sealed class FileStore(IQuerySession session, IFileStorage storage, TenantContext tenant) : IFileStore
{
    public async Task<StoredFileInfo?> FindReadableAsync(Guid id, Guid? userId, CancellationToken cancellationToken = default)
    {
        var file = await LoadReadableAsync(id, userId, cancellationToken);

        return file is null
            ? null
            : new StoredFileInfo
            {
                Id = file.Id,
                FileName = file.FileName,
                // A row stored before uploads were checked carries whatever type its client declared.
                ContentType = UploadTypes.IsExactly(file.ContentType) ? file.ContentType : "application/octet-stream",
                Size = file.Size,
            };
    }

    public async Task<Stream?> OpenReadableAsync(Guid id, Guid? userId, CancellationToken cancellationToken = default)
    {
        var file = await LoadReadableAsync(id, userId, cancellationToken);
        if (file is null)
        {
            return null;
        }

        // The storage hands back the whole object as one array, so the array is exposed and a
        // caller that wants the bytes does not copy them out of the stream.
        var bytes = await storage.GetAsync(file.StorageKey, cancellationToken);
        return bytes is null ? null : new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
    }

    private async Task<StoredFile?> LoadReadableAsync(Guid id, Guid? userId, CancellationToken ct)
    {
        var file = await session.LoadAsync<StoredFile>(id, ct);
        if (file is null || file.ParentFileId is not null)
        {
            return null;
        }

        return file.IsPublic || await FileOwnership.CanAccessAsync(session, tenant.Slug, userId, file, ct)
            ? file
            : null;
    }
}
