namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Reads the public files a module stores, for a caller that must not reference that module.
/// </summary>
/// <remarks>
/// <para>
/// <b>Public files only.</b> Both members hand out a file only when it is marked public, which is
/// a file anyone holding its URL can already download. A file that is not public reads as null,
/// exactly as a file that does not exist does, so a caller learns nothing about it. This is not a
/// general read: there is no caller here whose right to a private file could be checked. A read
/// of any file needs its own member with its own access rule.
/// </para>
/// <para>
/// <b>The tenant is the scope's.</b> No member takes a tenant. An id resolves only in the tenant of
/// the scope this was resolved from, so a file of another tenant reads as null.
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
    /// The record of a public file, or null when this tenant has no public file with that id. A
    /// cached resize of an image is not a file of its own and reads as null.
    /// </summary>
    Task<StoredFileInfo?> FindPublicAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The bytes of a public file from the start, or null when <see cref="FindPublicAsync"/> would
    /// answer null or the bytes are gone. The caller disposes the stream.
    /// </summary>
    Task<Stream?> OpenPublicAsync(Guid id, CancellationToken cancellationToken = default);
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
