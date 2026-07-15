namespace BarakoCMS.Files;

/// <summary>
/// A file stored in the database (Postgres via Marten). Suitable for low-to-moderate volumes of
/// small files — receipts, photos, documents. Large-scale or high-traffic blob storage should use
/// an object store instead.
/// </summary>
public class StoredFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public Guid UploadedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
