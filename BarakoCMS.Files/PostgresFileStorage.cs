using Marten;

namespace BarakoCMS.Files;

/// <summary>
/// The default storage: bytes in Postgres via Marten, one <see cref="FileBlob"/> per key. It serves
/// nothing publicly, so a public file stored here is delivered through the API (the public download
/// endpoint proxies the bytes) rather than a direct URL. Good for low volumes; point the S3 provider
/// at an object store for real public media at scale.
/// </summary>
public sealed class PostgresFileStorage : IFileStorage
{
    private readonly IDocumentSession _session;
    public PostgresFileStorage(IDocumentSession session) => _session = session;

    public string Provider => "postgres";

    public async Task<StoredObjectRef> PutAsync(Stream content, string key, string contentType, bool isPublic, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        _session.Store(new FileBlob { Id = key, Data = ms.ToArray() });
        await _session.SaveChangesAsync(ct);
        return new StoredObjectRef(key, null); /* Postgres proxies; no direct public URL */
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        var blob = await _session.LoadAsync<FileBlob>(key, ct);
        return blob?.Data;
    }

    public string? PublicUrl(string key, bool isPublic) => null;

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _session.Delete<FileBlob>(key);
        await _session.SaveChangesAsync(ct);
    }
}
