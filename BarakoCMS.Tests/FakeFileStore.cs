using System.Security.Claims;
using barakoCMS.Core.Interfaces;

namespace BarakoCMS.Tests;

/// <summary>
/// A file store held in memory, answering as BarakoCMS.Files does: a public file to anyone, a
/// private one to the user it belongs to, and it records every question it was asked.
/// </summary>
internal sealed class FakeFileStore : IFileStore
{
    private readonly Dictionary<Guid, (StoredFileInfo Info, bool IsPublic, Guid Owner)> _files = new();

    /// <summary>The number of ids in each call that read many public files, in order.</summary>
    public List<int> PublicBatches { get; } = new();

    /// <summary>The number of ids in each call that read many files for a caller, in order.</summary>
    public List<int> CallerBatches { get; } = new();

    /// <summary>One entry for each single read: "public" or "caller".</summary>
    public List<string> SingleReads { get; } = new();

    public Guid Add(
        bool isPublic,
        Guid owner = default,
        string fileName = "photo.png",
        string? alt = null,
        string? caption = null,
        bool withAddress = true)
    {
        var id = Guid.NewGuid();
        _files[id] = (
            new StoredFileInfo
            {
                Id = id,
                FileName = fileName,
                ContentType = "image/png",
                Size = 1234,
                PublicUrl = isPublic && withAddress ? $"/api/public/files/{id}" : null,
                Alt = alt,
                Caption = caption,
            },
            isPublic,
            owner);
        return id;
    }

    private StoredFileInfo? Public(Guid id) =>
        _files.TryGetValue(id, out var file) && file.IsPublic ? file.Info : null;

    private StoredFileInfo? For(Guid id, ClaimsPrincipal caller)
    {
        if (!_files.TryGetValue(id, out var file))
            return null;

        if (file.IsPublic)
            return file.Info;

        return caller.Identity is { IsAuthenticated: true }
               && Guid.TryParse(caller.FindFirst("UserId")?.Value, out var userId)
               && userId != Guid.Empty
               && userId == file.Owner
            ? file.Info
            : null;
    }

    public Task<StoredFileInfo?> FindPublicAsync(Guid id, CancellationToken cancellationToken = default)
    {
        SingleReads.Add("public");
        return Task.FromResult(Public(id));
    }

    public Task<Stream?> OpenPublicAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(null);

    public Task<StoredFileInfo?> FindAsync(Guid id, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        SingleReads.Add("caller");
        return Task.FromResult(For(id, caller));
    }

    public Task<IReadOnlyDictionary<Guid, StoredFileInfo>> FindPublicManyAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        PublicBatches.Add(ids.Count);

        IReadOnlyDictionary<Guid, StoredFileInfo> found = ids
            .Select(Public)
            .Where(file => file is not null)
            .ToDictionary(file => file!.Id, file => file!);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyDictionary<Guid, StoredFileInfo>> FindManyAsync(
        IReadOnlyCollection<Guid> ids, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        CallerBatches.Add(ids.Count);

        IReadOnlyDictionary<Guid, StoredFileInfo> found = ids
            .Select(id => For(id, caller))
            .Where(file => file is not null)
            .ToDictionary(file => file!.Id, file => file!);
        return Task.FromResult(found);
    }

    /// <summary>A signed-in user, with the claims the token issuer writes.</summary>
    public static ClaimsPrincipal User(Guid userId, string role = "User") =>
        new(new ClaimsIdentity(
            new[] { new Claim("UserId", userId.ToString()), new Claim(ClaimTypes.Role, role) },
            "Test",
            "Username",
            ClaimTypes.Role));
}
