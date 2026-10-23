using barakoCMS.Models;

namespace BarakoCMS.Notifications.Features.List;

internal sealed class ListNotificationsRequest : PaginatedRequest
{
    /// <summary>When true, only notifications not yet read.</summary>
    public bool UnreadOnly { get; set; }
}

internal sealed class NotificationResponse
{
    public Guid Id { get; set; }
    public Guid RecipientUserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public Dictionary<string, object>? Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }

    internal static NotificationResponse From(Notification n) => new()
    {
        Id = n.Id,
        RecipientUserId = n.RecipientUserId,
        Type = n.Type,
        Title = n.Title,
        Body = n.Body,
        Payload = n.Payload,
        CreatedAt = n.CreatedAt,
        ReadAt = n.ReadAt,
    };
}
