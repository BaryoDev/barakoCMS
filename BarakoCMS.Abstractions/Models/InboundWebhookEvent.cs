namespace barakoCMS.Models;

/// <summary>
/// A provider event the inbound webhook receiver has already processed. Its presence is how a
/// replay is told from a first delivery.
/// </summary>
/// <remarks>
/// The id is <c>{provider}:{eventId}</c>, so two providers that happen to reuse an id do not collide.
/// Global, like the receivers it serves: a webhook carries no tenant.
/// </remarks>
public class InboundWebhookEvent
{
    public string Id { get; set; } = string.Empty;

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public static string IdFor(string provider, string eventId) => $"{provider}:{eventId}";
}
