namespace BarakoCMS.Comments.Features.Post;

internal sealed class Request
{
    public string TargetType { get; set; } = string.Empty;

    public Guid TargetId { get; set; }

    public string Body { get; set; } = string.Empty;

    /// <summary>The comment being replied to. It must be on the same record.</summary>
    public Guid? ParentId { get; set; }
}

internal sealed record Response(
    Guid Id,
    string TargetType,
    Guid TargetId,
    Guid? ParentId,
    Guid AuthorId,
    string AuthorName,
    string Body,
    DateTime CreatedAt)
{
    public static Response From(Comment c) =>
        new(c.Id, c.TargetType, c.TargetId, c.ParentId, c.AuthorId, c.AuthorName, c.Body, c.CreatedAt);
}
