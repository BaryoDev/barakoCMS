using Microsoft.Extensions.Configuration;

namespace barakoCMS.Features.Public.Events;

/// <summary>The event names a subscriber sees on <c>GET /api/public/events</c>.</summary>
internal static class ContentChangeEvents
{
    public const string Published = "content.published";
    public const string Updated = "content.updated";
    public const string Unpublished = "content.unpublished";

    /// <summary>
    /// Written when nothing else has been for a while, so a proxy does not close an idle
    /// connection. A comment line, which an <c>EventSource</c> never dispatches.
    /// </summary>
    public const string KeepAliveComment = ": keepalive\n\n";
}

/// <summary>
/// One change, already projected for anonymous delivery. What goes into <see cref="Payload"/> is
/// what a subscriber receives, and nothing on this record is consulted for masking.
/// </summary>
internal sealed record ContentChange(string EventName, Guid Id, string ContentType, string? Slug, object Payload);

/// <summary>
/// The body of a <c>content.unpublished</c> event. Only the identity, because the entry is no
/// longer public and a subscriber must be able to drop it without being handed its fields.
/// </summary>
internal sealed record UnpublishedPayload(Guid Id, string ContentType, string? Slug);

/// <summary>
/// The event stream's configuration, read at first use. Off by default: an anonymous long-lived
/// connection is a resource anybody on the internet can hold, so a deployment opts in.
/// </summary>
internal sealed class ContentEventsOptions
{
    public const string EnabledKey = "Delivery:Events:Enabled";
    public const string MaxConnectionsKey = "Delivery:Events:MaxConnections";
    public const string KeepAliveSecondsKey = "Delivery:Events:KeepAliveSeconds";

    public const int DefaultMaxConnections = 100;
    public const int DefaultKeepAliveSeconds = 15;

    /// <summary>
    /// How many changes a connection may fall behind before the oldest is dropped. Small on
    /// purpose: the payloads are whole entries, and a subscriber that cannot keep up with sixty-four
    /// of them is not going to catch up by being given more.
    /// </summary>
    public const int BufferPerConnection = 64;

    public bool Enabled { get; init; }

    /// <summary>Open streams across all tenants of this instance; the next one gets 503.</summary>
    public int MaxConnections { get; init; } = DefaultMaxConnections;

    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(DefaultKeepAliveSeconds);

    public static ContentEventsOptions FromConfiguration(IConfiguration configuration) => new()
    {
        Enabled = configuration.GetValue(EnabledKey, false),
        MaxConnections = Math.Max(1, configuration.GetValue(MaxConnectionsKey, DefaultMaxConnections)),
        KeepAlive = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue(KeepAliveSecondsKey, DefaultKeepAliveSeconds))),
    };
}
