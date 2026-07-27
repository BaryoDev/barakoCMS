namespace BarakoCMS.Files;

/*
 * Storage abstraction so file bytes can live in Postgres (the default) or an S3-compatible object
 * store (the BarakoCMS.Files.S3 provider), chosen by configuration. Metadata (name, type, size,
 * which store, the key) stays in Postgres as a StoredFile record; only the bytes move. A provider
 * that can serve bytes directly and publicly (S3 with a public bucket or CDN) returns a PublicUrl the
 * frontend uses as an <img> src; a provider that can't (Postgres) returns null and the bytes are
 * delivered through the API instead.
 */
public interface IFileStorage
{
    /// <summary>A short provider id stored on the file so reads know where its bytes live (e.g. "postgres", "s3").</summary>
    string Provider { get; }

    /// <summary>
    /// Stores <paramref name="content"/> and returns the storage key plus, for a public-capable
    /// provider, a directly-usable public URL (else null).
    /// </summary>
    Task<StoredObjectRef> PutAsync(Stream content, string key, string contentType, bool isPublic, CancellationToken ct = default);

    /// <summary>Reads the bytes for a key, or null if absent. Used when the API proxies delivery.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>A directly-usable public URL for a public object, or null if this provider proxies instead.</summary>
    string? PublicUrl(string key, bool isPublic);

    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>The result of storing an object: its key and (if the provider serves publicly) a URL.</summary>
public sealed record StoredObjectRef(string Key, string? PublicUrl);
