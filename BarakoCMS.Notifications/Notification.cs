namespace BarakoCMS.Notifications;

/// <summary>
/// Something a module wants one user to know. Stored per tenant, so a notification raised in one
/// tenant is not visible from another even to the same user.
/// </summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RecipientUserId { get; set; }

    /// <summary>A stable, dotted name the raising module owns, such as <c>entry.needs-approval</c>.</summary>
    public string Type { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Body { get; set; }

    /// <summary>Whatever the raising module wants a client to be able to link back to.</summary>
    public Dictionary<string, object>? Payload { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Null while unread. Set once, by the first read.</summary>
    public DateTimeOffset? ReadAt { get; set; }
}
