using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;

namespace barakoCMS.Infrastructure.Services;

/// <summary>What a <c>file</c> field resolves to in a response.</summary>
/// <param name="Url">
/// Where anyone can fetch the file: an absolute URL, or a path that starts with a slash, to be
/// joined to the address the API is served from. Null for a file that is not public.
/// </param>
/// <param name="FileName">The name the file was uploaded with. It came from a client.</param>
internal sealed record ResolvedFile(
    Guid Id,
    string? Url,
    string FileName,
    string ContentType,
    long Size,
    string? Alt,
    string? Caption)
{
    public static ResolvedFile From(StoredFileInfo file) =>
        new(file.Id, file.PublicUrl, file.FileName, file.ContentType, file.Size, file.Alt, file.Caption);
}

/// <summary>
/// Reads the files a response's <c>file</c> fields name, for all of its entries at once.
/// </summary>
/// <remarks>
/// One read of the file store for every <see cref="IdsPerRead"/> distinct ids, not one per field.
/// A page holds at most <c>PaginatedRequest.MaxPageSize</c> entries, so a page of a hundred entries
/// with five file fields each is one read. Past the bound nothing is dropped: the ids are read in
/// further batches of the same size.
/// </remarks>
internal static class FileFieldResolver
{
    public const int IdsPerRead = 500;

    /// <summary>Adds the ids the entry's file fields hold. A value that is not an id adds nothing.</summary>
    public static void Collect(IReadOnlyDictionary<string, object> data, HashSet<string> fileFields, HashSet<Guid> into)
    {
        if (fileFields.Count == 0)
            return;

        foreach (var (key, value) in data)
        {
            if (fileFields.Contains(key) && FileFields.TryReadId(value, out var id))
                into.Add(id);
        }
    }

    /// <summary>
    /// The files among <paramref name="ids"/> that may be shown: the public ones when
    /// <paramref name="caller"/> is null, and otherwise the ones that caller may download.
    /// </summary>
    /// <remarks>
    /// Empty on a host with no module that stores files, so an entry there still reads and its
    /// file fields resolve to nothing.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<Guid, StoredFileInfo>> ReadAsync(
        IFileStore? files, IReadOnlyCollection<Guid> ids, ClaimsPrincipal? caller, CancellationToken ct)
    {
        var found = new Dictionary<Guid, StoredFileInfo>();
        if (ids.Count == 0 || files is null or NoFileStore)
            return found;

        foreach (var batch in ids.Chunk(IdsPerRead))
        {
            var read = caller is null
                ? await files.FindPublicManyAsync(batch, ct)
                : await files.FindManyAsync(batch, caller, ct);

            foreach (var (id, file) in read)
                found[id] = file;
        }

        return found;
    }

    /// <summary>
    /// The files one entry's fields resolve to, keyed by the entry's own data key. A field whose
    /// file is not in <paramref name="found"/> has no entry.
    /// </summary>
    public static Dictionary<string, ResolvedFile>? Resolved(
        IReadOnlyDictionary<string, object> data,
        HashSet<string> fileFields,
        IReadOnlyDictionary<Guid, StoredFileInfo> found)
    {
        Dictionary<string, ResolvedFile>? resolved = null;

        foreach (var (key, value) in data)
        {
            if (fileFields.Contains(key)
                && FileFields.TryReadId(value, out var id)
                && found.TryGetValue(id, out var file))
            {
                (resolved ??= new())[key] = ResolvedFile.From(file);
            }
        }

        return resolved;
    }
}
