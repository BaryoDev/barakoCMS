namespace BarakoCMS.Notifications;

/// <summary>What a module raises. <see cref="Body"/> and <see cref="Payload"/> are optional.</summary>
public sealed record RaiseNotification(Guid RecipientUserId, string Type, string Title)
{
    public string? Body { get; init; }

    public Dictionary<string, object>? Payload { get; init; }
}

/// <summary>
/// How a module tells a user something happened. Resolve it from the container and call
/// <see cref="RaiseAsync"/>; the notification is stored in the current tenant and then handed to
/// every registered <see cref="INotificationSink"/>.
/// </summary>
public interface INotificationService
{
    /// <summary>Stores the notification unread and returns its id.</summary>
    /// <remarks>
    /// A sink that throws is logged and skipped. The in-app copy is already saved by then, so a down
    /// channel never loses the notification or fails the module that raised it.
    /// </remarks>
    Task<Guid> RaiseAsync(RaiseNotification notification, CancellationToken ct = default);
}

/// <summary>
/// A channel a notification is also delivered through, beside the in-app store, which is always on.
/// Register one with <c>services.AddScoped&lt;INotificationSink, YourSink&gt;()</c>.
/// </summary>
public interface INotificationSink
{
    Task DeliverAsync(Notification notification, CancellationToken ct);
}
