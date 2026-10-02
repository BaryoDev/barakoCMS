using barakoCMS.Core.Interfaces;

namespace barakoCMS.Infrastructure.Services;

/// <summary>The file store of a host with no module that stores files. Every member refuses.</summary>
internal sealed class NoFileStore : IFileStore
{
    internal const string Message =
        "No module that stores files is enabled. Enable BarakoCMS.Files and restart.";

    public Task<StoredFileInfo?> FindReadableAsync(Guid id, Guid? userId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public Task<Stream?> OpenReadableAsync(Guid id, Guid? userId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);
}
