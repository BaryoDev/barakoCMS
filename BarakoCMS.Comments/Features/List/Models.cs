using barakoCMS.Models;

namespace BarakoCMS.Comments.Features.List;

/// <summary>The record whose thread to read, and a page of it. The page size is capped by core.</summary>
internal sealed class Request : PaginatedRequest
{
    public string TargetType { get; set; } = string.Empty;

    public Guid TargetId { get; set; }
}

internal sealed record Item(
    Guid Id,
    string TargetType,
    Guid TargetId,
    Guid? ParentId,
    Guid AuthorId,
    string AuthorName,
    string Body,
    DateTime CreatedAt)
{
    public static Item From(Comment c) =>
        new(c.Id, c.TargetType, c.TargetId, c.ParentId, c.AuthorId, c.AuthorName, c.Body, c.CreatedAt);
}
