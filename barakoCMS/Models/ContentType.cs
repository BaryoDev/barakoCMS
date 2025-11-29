namespace barakoCMS.Models;

public class ContentType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty; // e.g., "Article", "Product"
    public string Slug { get; set; } = string.Empty; // e.g., "article"
    public Dictionary<string, string> Fields { get; set; } = new(); // Name -> Type (e.g., "Title" -> "string")
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
