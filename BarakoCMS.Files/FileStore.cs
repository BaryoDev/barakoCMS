using barakoCMS.Core.Interfaces;
using Marten;

namespace BarakoCMS.Files;

/// <summary>
/// The core's way to read a public file without referencing this module.
/// </summary>
/// <remarks>
/// It answers the question the public download route answers, and nothing more: a file is handed
/// over only when it is marked public, and a private file reads as absent. There is no caller to
/// apply <see cref="FileOwnership"/> to, so no private file leaves through here.
///
/// Both the record and the bytes are read through the scope's own session and storage, which are
/// opened for the scope's tenant, so an id of another tenant loads nothing. A cached resize is
/// absent here for the reason both download routes refuse one: it has no access rules of its own.
/// </remarks>
internal sealed class FileStore(IQuerySession session, IFileStorage storage) : IFileStore
{
    public async Task<StoredFileInfo?> FindPublicAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var file = await LoadPublicAsync(id, cancellationToken);

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
}
