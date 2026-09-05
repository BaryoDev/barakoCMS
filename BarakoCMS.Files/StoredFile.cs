namespace BarakoCMS.Files;

/// <summary>
/// Metadata for an uploaded file. The bytes live wherever the configured <see cref="IFileStorage"/>
/// put them (Postgres by default, or any S3-compatible store: AWS S3, Cloudflare R2, MinIO); this
/// record holds only the metadata and the storage key, so a read knows where to fetch from. Public
/// files carry a direct <see cref="PublicUrl"/> when the store serves publicly; for Postgres it is
/// null and the bytes are delivered through the API.
/// </summary>
public class StoredFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }

    /// <summary>Which storage holds the bytes ("postgres", "s3"), so reads route correctly.</summary>
    public string Provider { get; set; } = "postgres";

    /// <summary>The key within that storage.</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>Whether the file is intended for public (anonymous) delivery. Default false: fail closed.</summary>
    public bool IsPublic { get; set; }

    /// <summary>A directly-usable public URL for public objects on a public-capable store; else null.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// The original this record was derived from, for a cached resize. Null on an upload.
    /// </summary>
    /// <remarks>
    /// A derived record is not addressable: both download routes refuse an id whose ParentFileId is
    /// set, and a variant is only ever reached as <c>?w=</c> on its original. That is what keeps a
    /// variant from having access rules of its own to get out of step with the file it came from.
    /// </remarks>
    public Guid? ParentFileId { get; set; }

    /// <summary>The width a derived record was resized to, in pixels. Null on an upload.</summary>
    public int? VariantWidth { get; set; }

    /// <summary>What a screen reader says for the image. Null until an editor writes one.</summary>
    public string? Alt { get; set; }

    /// <summary>Text shown alongside the file. Null until an editor writes one.</summary>
    public string? Caption { get; set; }

    public Guid UploadedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Raw bytes for the Postgres storage provider, keyed by the storage key. Kept separate from
/// <see cref="StoredFile"/> so listing or reading metadata never drags the blob along.
/// </summary>
public class FileBlob
{
    public string Id { get; set; } = string.Empty; // the storage key
    public byte[] Data { get; set; } = Array.Empty<byte>();
}
