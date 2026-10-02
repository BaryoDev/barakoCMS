using System.Security.Claims;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Stores, reads and deletes the files a module keeps, for a caller that must not reference that
/// module.
/// </summary>
/// <remarks>
/// <para>
/// <b>Public files need no caller.</b> <see cref="FindPublicAsync"/>, <see cref="OpenPublicAsync"/>
/// and <see cref="PublicUrlAsync"/> hand out a file only when it is marked public, which is a file
/// anyone holding its URL can already download. A file that is not public reads as null, exactly as
/// a file that does not exist does, so a caller learns nothing about it. These are the members for
/// work that runs with no user, such as a workflow.
/// </para>
/// <para>
/// <b>Any other read names its caller.</b> <see cref="FindAsync"/>, <see cref="OpenAsync"/> and
/// <see cref="DeleteAsync"/> take the signed-in user the work is done for and give that user what
/// the store's own API would: a private file is read by the user it belongs to or by an account
/// administering the tenant, and by nobody else. There is no member that reads a private file for
/// no user.
/// </para>
/// <para>
/// <b>The caller is the current request's.</b> Pass the principal of the request the scope serves,
/// which the request pipeline has already let in. A store refuses a principal that is not signed
/// in, comes from an API key or was issued for another tenant, and checks nothing else about it:
/// not whether its token was revoked since, not whether its tenant is still active, not device
/// trust. A principal kept from an earlier request or rebuilt from a stored token gets none of
/// those checks.
/// </para>
/// <para>
/// <b>A save and a delete commit.</b> <see cref="SaveAsync"/> and <see cref="DeleteAsync"/> commit
/// their own work through the scope's unit of work before they return. Call them before staging
/// anything else in the scope: BarakoCMS.Files throws <see cref="InvalidOperationException"/> when
/// work is already staged, so that it never commits a caller's changes for a file it then refuses.
/// Inside a content batch nothing commits until the batch does, and bytes written to or removed
/// from an object store do not roll back with it.
/// </para>
/// <para>
/// <b>The tenant is the scope's.</b> No member takes a tenant. An id resolves only in the tenant of
/// the scope this was resolved from, a file is stored into that tenant, and a file of another
/// tenant reads as null.
/// </para>
/// <para>
/// <b>A module implements this.</b> BarakoCMS.Files does. With no such module enabled the host
/// registers a default whose every member throws <see cref="InvalidOperationException"/> naming the
/// module to enable. Every member after the first two has a default, so a store written against
/// the first two still compiles: the two that read many files ask the single reads, and the rest
/// throw <see cref="NotSupportedException"/>.
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

    /// <summary>
    /// Where anyone can fetch a public file, or null when <see cref="FindPublicAsync"/> would answer
    /// null. It is an absolute URL when the store serves the file itself, and otherwise a path that
    /// starts with a slash, to be joined to the address the API is served from.
    /// </summary>
    Task<string?> PublicUrlAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not give a public address for a file.");

    /// <summary>
    /// The record of a file <paramref name="caller"/> may download: a public file, or a private one
    /// that belongs to that user or that the user administers. Null for any other file, and for an
    /// id this tenant does not have.
    /// </summary>
    Task<StoredFileInfo?> FindAsync(Guid id, ClaimsPrincipal caller, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not read a file for a caller.");

    /// <summary>
    /// The bytes of a file from the start, or null when <see cref="FindAsync"/> would answer null
    /// or the bytes are gone. The caller disposes the stream.
    /// </summary>
    Task<Stream?> OpenAsync(Guid id, ClaimsPrincipal caller, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not read a file for a caller.");

    /// <summary>
    /// Stores a new file in the scope's tenant and commits it.
    /// </summary>
    /// <remarks>
    /// The store applies the checks it applies to an upload (what a file may be, how large, and a
    /// virus scan where one is configured), and a file that fails one is not stored:
    /// <see cref="FileSaveResult.File"/> is null and <see cref="FileSaveResult.Refused"/> says why.
    /// Nobody's right to store is checked here. The caller decides who may reach the code that
    /// stores, and whether the file may be public.
    /// </remarks>
    Task<FileSaveResult> SaveAsync(FileToStore file, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not store files.");

    /// <summary>
    /// Deletes a file and its bytes for <paramref name="caller"/>, who has to be allowed to delete
    /// it through the store's own API. While an entry's data still names the file the answer is
    /// <see cref="FileDeleteResult.InUse"/> and nothing is deleted, unless <paramref name="force"/>
    /// is set.
    /// </summary>
    Task<FileDeleteResult> DeleteAsync(Guid id, ClaimsPrincipal caller, bool force = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not delete files.");

    /// <summary>
    /// The public files among <paramref name="ids"/>, keyed by id: each one
    /// <see cref="FindPublicAsync"/> would answer, and no entry for any other id.
    /// </summary>
    /// <remarks>
    /// For a caller resolving many files at once, such as a page of entries. BarakoCMS.Files
    /// answers in one read however many ids it is given, so the caller bounds how many it asks for.
    /// The default asks <see cref="FindPublicAsync"/> once for each distinct id.
    /// </remarks>
    async Task<IReadOnlyDictionary<Guid, StoredFileInfo>> FindPublicManyAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var found = new Dictionary<Guid, StoredFileInfo>();
        foreach (var id in ids)
        {
            if (!found.ContainsKey(id) && await FindPublicAsync(id, cancellationToken) is { } file)
            {
                found[id] = file;
            }
        }

        return found;
    }

    /// <summary>
    /// The files among <paramref name="ids"/> that <paramref name="caller"/> may download, keyed by
    /// id: each one <see cref="FindAsync"/> would answer, and no entry for any other id.
    /// </summary>
    /// <remarks>
    /// The same bound as <see cref="FindPublicManyAsync"/>. The default asks
    /// <see cref="FindAsync"/> once for each distinct id.
    /// </remarks>
    async Task<IReadOnlyDictionary<Guid, StoredFileInfo>> FindManyAsync(
        IReadOnlyCollection<Guid> ids, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var found = new Dictionary<Guid, StoredFileInfo>();
        foreach (var id in ids)
        {
            if (!found.ContainsKey(id) && await FindAsync(id, caller, cancellationToken) is { } file)
            {
                found[id] = file;
            }
        }

        return found;
    }
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

    /// <summary>
    /// What <see cref="IFileStore.PublicUrlAsync"/> answers for this file: null for a file that is
    /// not public, and for a store that does not fill it in.
    /// </summary>
    public string? PublicUrl { get; init; }

    /// <summary>What a screen reader says for the image, when an editor wrote it. It is the editor's text, not markup.</summary>
    public string? Alt { get; init; }

    /// <summary>Text shown alongside the file, when an editor wrote it. It is the editor's text, not markup.</summary>
    public string? Caption { get; init; }
}

/// <summary>A file to hand to <see cref="IFileStore.SaveAsync"/>.</summary>
public sealed class FileToStore
{
    /// <summary>The bytes, read from the stream's current position to its end. The caller disposes the stream.</summary>
    public required Stream Content { get; init; }

    /// <summary>The name to keep. Only the part after the last path separator is stored.</summary>
    public required string FileName { get; init; }

    /// <summary>The media type the bytes are. The store checks the bytes against it.</summary>
    public required string ContentType { get; init; }

    /// <summary>Whether anyone may download the file without signing in. Private unless set.</summary>
    public bool IsPublic { get; init; }

    /// <summary>
    /// The user the file belongs to, who may download it through the store's API as if they had
    /// uploaded it. Left empty, the file belongs to no user and only an account administering the
    /// tenant reads it while it is private.
    /// </summary>
    public Guid Owner { get; init; }

    /// <summary>
    /// The user who supplied the file, when one did. It is named as the actor if the store records
    /// that it refused the file, and decides nothing else. Leave it null for a file no user sent,
    /// such as one a job produced.
    /// </summary>
    public Guid? SuppliedBy { get; init; }
}

/// <summary>What <see cref="IFileStore.SaveAsync"/> did: the stored file, or the reason it was not stored.</summary>
public sealed class FileSaveResult
{
    /// <summary>The stored file, or null when it was refused.</summary>
    public StoredFileInfo? File { get; init; }

    /// <summary>Why the file was not stored, or null when it was. Safe to show to the person who supplied the file.</summary>
    public string? Refused { get; init; }

    /// <summary>
    /// Whether the same file could be stored on a later attempt. True only when the refusal says
    /// nothing about the file itself, such as a virus scanner that did not answer.
    /// </summary>
    public bool CanRetry { get; init; }
}

/// <summary>What <see cref="IFileStore.DeleteAsync"/> did.</summary>
public enum FileDeleteResult
{
    /// <summary>The file, its cached resizes and their bytes are gone.</summary>
    Deleted = 0,

    /// <summary>This tenant has no such file, or the caller may not delete it. The two are not told apart.</summary>
    NotFound = 1,

    /// <summary>An entry's data still names the file, and <c>force</c> was not set.</summary>
    InUse = 2,
}
