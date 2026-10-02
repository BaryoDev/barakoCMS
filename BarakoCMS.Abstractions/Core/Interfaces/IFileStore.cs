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
/// <b>The module decides who may read.</b> Every member takes the user the read is for and answers
/// only when the module's own download rule lets that user have the file, or the file is public.
/// With no user, only a public file is readable. The caller never learns why a file was not
/// handed over: absent, another tenant's and not allowed all read as null.
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
    /// The record of an uploaded file that <paramref name="userId"/> may read, or null. A cached
    /// resize of an image is not a file of its own and reads as null.
    /// </summary>
    /// <param name="id">The file's id.</param>
    /// <param name="userId">The user the read is for, or null when there is none.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<StoredFileInfo?> FindReadableAsync(Guid id, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The bytes of a file <paramref name="userId"/> may read, from the start, or null when
    /// <see cref="FindReadableAsync"/> would answer null or the bytes are gone. The caller disposes
    /// the stream.
    /// </summary>
    /// <param name="id">The file's id.</param>
    /// <param name="userId">The user the read is for, or null when there is none.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<Stream?> OpenReadableAsync(Guid id, Guid? userId, CancellationToken cancellationToken = default);
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
