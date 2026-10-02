using barakoCMS.Core.Interfaces;

namespace barakoCMS.Infrastructure.Services;

/// <summary>The file store of a host with no module that stores files. Every member refuses.</summary>
internal sealed class NoFileStore : IFileStore
{
    internal const string Message =
        "No module that stores files is enabled. Enable BarakoCMS.Files and restart.";

    public Task<StoredFileInfo?> FindPublicAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public Task<Stream?> OpenPublicAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public Task<string?> PublicUrlAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public Task<StoredFileInfo?> FindAsync(Guid id, System.Security.Claims.ClaimsPrincipal caller, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public Task<Stream?> OpenAsync(Guid id, System.Security.Claims.ClaimsPrincipal caller, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public Task<FileSaveResult> SaveAsync(FileToStore file, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public Task<FileDeleteResult> DeleteAsync(Guid id, System.Security.Claims.ClaimsPrincipal caller, bool force = false, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public Task<IReadOnlyDictionary<Guid, StoredFileInfo>> FindPublicManyAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public Task<IReadOnlyDictionary<Guid, StoredFileInfo>> FindManyAsync(
        IReadOnlyCollection<Guid> ids, System.Security.Claims.ClaimsPrincipal caller, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);
}
