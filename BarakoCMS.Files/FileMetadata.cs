namespace BarakoCMS.Files;

/// <summary>
/// What the list, the metadata read and the update answer with. The bytes are a separate route.
/// </summary>
public class FileMetadata
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsPublic { get; set; }
    public string? PublicUrl { get; set; }
    public string? Alt { get; set; }
    public string? Caption { get; set; }
    public Guid UploadedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    internal static FileMetadata From(StoredFile file) => new()
    {
        Id = file.Id,
        FileName = file.FileName,
        ContentType = file.ContentType,
        Size = file.Size,
        IsPublic = file.IsPublic,
        PublicUrl = file.PublicUrl,
        Alt = file.Alt,
        Caption = file.Caption,
        UploadedBy = file.UploadedBy,
        CreatedAt = file.CreatedAt,
    };
}
