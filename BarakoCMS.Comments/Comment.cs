namespace BarakoCMS.Comments;

/// <summary>
/// One comment or note on a record. Stored per tenant like every barakoCMS document, so a comment
/// written in one tenant is never read from another.
/// </summary>
public sealed class Comment
{
    public Guid Id { get; set; }

    /// <summary>The kind of record, as an <see cref="ICommentTarget"/> names it, for example <c>content</c>.</summary>
    public string TargetType { get; set; } = string.Empty;

    public Guid TargetId { get; set; }

    /// <summary>The comment this one replies to, or null for the start of a thread.</summary>
    public Guid? ParentId { get; set; }

    public Guid AuthorId { get; set; }

    /// <summary>The author's display name when the comment was written.</summary>
    public string AuthorName { get; set; } = string.Empty;

    /// <summary>Plain text, exactly as typed. Never HTML, and never encoded by this module.</summary>
    public string Body { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Limits a comment is held to.</summary>
public static class CommentLimits
{
    public const int MaxBodyLength = 5000;

    public const int MaxTargetTypeLength = 64;
}
