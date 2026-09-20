using Marten;

namespace BarakoCMS.Files;

/// <summary>
/// The default storage: bytes in Postgres via Marten, one <see cref="FileBlob"/> per key. It serves
/// nothing publicly, so a public file stored here is delivered through the API (the public download
/// endpoint proxies the bytes) rather than a direct URL. Good for low volumes; point the S3 provider
/// at an object store for real public media at scale.
/// </summary>
public sealed class PostgresFileStorage(IDocumentSession session) : IFileStorage
{
    public string Provider => "postgres";

    public async Task<StoredObjectRef> PutAsync(Stream content, string key, string contentType, bool isPublic, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        session.Store(new FileBlob { Id = key, Data = ms.ToArray() });
        await session.SaveChangesAsync(ct);
        return new StoredObjectRef(key, null); /* Postgres proxies; no direct public URL */
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        var blob = await session.LoadAsync<FileBlob>(key, ct);
        return blob?.Data;
    }

    public string? PublicUrl(string key, bool isPublic) => null;

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        session.Delete<FileBlob>(key);
        await session.SaveChangesAsync(ct);
    }
}
