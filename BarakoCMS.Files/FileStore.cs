using barakoCMS.Core.Interfaces;
using Marten;

namespace BarakoCMS.Files;

/// <summary>
/// The core's way to read a stored file without referencing this module.
/// </summary>
/// <remarks>
/// Both the record and the bytes are read through the scope's own session and storage, which are
/// opened for the scope's tenant, so an id of another tenant loads nothing. A cached resize is
/// absent here for the reason both download routes refuse one: it has no access rules of its own.
/// </remarks>
internal sealed class FileStore(IQuerySession session, IFileStorage storage) : IFileStore
{
    public async Task<StoredFileInfo?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var file = await LoadAsync(id, cancellationToken);

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

    public async Task<Stream?> OpenReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var file = await LoadAsync(id, cancellationToken);
        if (file is null)
        {
            return null;
        }

        var bytes = await storage.GetAsync(file.StorageKey, cancellationToken);
        return bytes is null ? null : new MemoryStream(bytes, writable: false);
    }

    private async Task<StoredFile?> LoadAsync(Guid id, CancellationToken ct)
    {
        var file = await session.LoadAsync<StoredFile>(id, ct);
        return file is null || file.ParentFileId is not null ? null : file;
    }
}
