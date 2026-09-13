namespace barakoCMS.Features.Content.GetBySlug;

internal class Request
{
    /// <summary>The content type name, as in <c>GET /api/public/{type}/{slug}</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Matched case-insensitively against the type's slug field, the same way delivery matches it.</summary>
    public string Slug { get; set; } = string.Empty;
}
