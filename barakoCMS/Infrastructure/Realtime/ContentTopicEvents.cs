using barakoCMS.Events;

namespace barakoCMS.Infrastructure.Realtime;

/// <summary>
/// The content event types a content topic carries. Every one of them changes what a reader of the
/// entry sees, so a client that holds the topic reads the entry again when one is appended.
/// </summary>
internal static class ContentTopicEvents
{
    public static readonly Type[] Types =
    [
        typeof(ContentCreated), typeof(ContentUpdated), typeof(ContentStatusChanged),
        typeof(ContentTransitioned), typeof(ContentFieldChanged), typeof(ContentSensitivityChanged),
        typeof(ContentFieldSensitivityChanged),
    ];
}
