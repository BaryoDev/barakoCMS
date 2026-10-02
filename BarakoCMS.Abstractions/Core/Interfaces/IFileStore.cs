namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Reads the files a module stores, for a caller that must not reference that module.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tenant is the scope's.</b> No member takes a tenant. An id resolves only in the tenant of
/// the scope this was resolved from, so a file of another tenant reads as absent.
/// </para>
/// <para>
/// <b>It decides nothing about access.</b> There is no caller here to check, so every file of the
/// tenant is readable through it. Whoever calls it owns the rule for which files may leave, and has
/// to apply that rule before asking.
/// </para>
/// <para>
/// <b>A module implements this.</b> BarakoCMS.Files does. With no such module enabled the host
/// registers a default whose every member throws <see cref="InvalidOperationException"/> naming the
/// module to enable.
/// </para>
/// </remarks>
public interface IFileStore
{
    /// <summary>
    /// The record of an uploaded file, or null when this tenant has no such file. A cached resize
    /// of an image is not a file of its own and reads as absent.
    /// </summary>
    Task<StoredFileInfo?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The file's bytes from the start, or null when <see cref="FindAsync"/> would answer null or
    /// the bytes are gone. The caller disposes the stream.
    /// </summary>
    Task<Stream?> OpenReadAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>What a stored file is, without its bytes.</summary>
public sealed class StoredFileInfo
{
    public required Guid Id { get; init; }

    /// <summary>The name the file was uploaded with. It came from a client, so it is not safe to put in a header as it is.</summary>
    public required string FileName { get; init; }

    /// <summary>A media type the store checked the bytes against, or <c>application/octet-stream</c>.</summary>
    public required string ContentType { get; init; }

    /// <summary>The size in bytes recorded at upload.</summary>
    public required long Size { get; init; }
}
