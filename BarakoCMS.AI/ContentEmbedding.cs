namespace BarakoCMS.AI;

/// <summary>
/// A stored vector for one published content entry. Id == the content's id (one embedding per entry,
/// upserted). Multi-tenanted like all content, so a tenant only ever searches its own embeddings.
/// Only the slug and a public title are kept alongside the vector — never a Sensitive field.
/// </summary>
public sealed class ContentEmbedding
{
    public Guid Id { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string? Slug { get; set; }
    public string Title { get; set; } = string.Empty;
    public float[] Vector { get; set; } = Array.Empty<float>();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
