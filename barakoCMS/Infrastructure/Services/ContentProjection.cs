using barakoCMS.Events;
using barakoCMS.Models;
using JasperFx.Events;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Folds a content stream into the <see cref="Content"/> document that everything reads.
/// </summary>
/// <remarks>
/// This is the projection the event-sourced mode names. For a type whose policy says the stream is
/// the source of truth, the document is produced here rather than by the write handler mutating the
/// copy it loaded, and that difference is the feature: a document produced from the stream can be
/// deleted and rebuilt, and one that is not, cannot.
///
/// It is a fold rather than a Marten projection registration on purpose. A registered inline
/// projection runs for every content stream in the store, so it would make document-mode types
/// rebuildable too, and then the paired control test ("rebuild a document-mode type and assert the
/// documents do NOT come back") would pass whatever the flag said. The routing decision belongs to
/// <c>IContentSourcingPolicy</c>, and only one caller may act on it.
///
/// Every event is replayed with its own timestamp. Reading the clock here would stamp a rebuild
/// with the time of the rebuild, and nothing about the result would look wrong.
/// </remarks>
internal static class ContentProjection
{
    /// <summary>Rebuilds a document from a whole stream, or null when the stream is empty.</summary>
    public static Content? Fold(IReadOnlyList<IEvent> events)
    {
        if (events.Count == 0)
        {
            return null;
        }

        var content = new Content();
        foreach (var e in events)
        {
            Apply(content, e.Data, e.Timestamp.UtcDateTime);
        }

        return content;
    }

    /// <summary>
    /// Routes an event to the matching <c>Content.Apply</c> overload.
    /// </summary>
    /// <remarks>
    /// The unmatched case throws rather than doing nothing. An event with no projection would append
    /// cleanly and leave the document unchanged, which reads as a successful save and is only
    /// visible later as a document that disagrees with its own history. Failing the write is the
    /// louder and cheaper outcome.
    /// </remarks>
    public static void Apply(Content content, object @event, DateTime occurredAt)
    {
        switch (@event)
        {
            case ContentCreated created:
                content.Apply(created, occurredAt);
                break;
            case ContentUpdated updated:
                content.Apply(updated, occurredAt);
                break;
            case ContentStatusChanged statusChanged:
                content.Apply(statusChanged, occurredAt);
                break;
            case ContentScheduled scheduled:
                content.Apply(scheduled, occurredAt);
                break;
            case ContentTransitioned transitioned:
                content.Apply(transitioned, occurredAt);
                break;
            case ContentSensitivityChanged sensitivityChanged:
                content.Apply(sensitivityChanged, occurredAt);
                break;
            case ContentFieldSensitivityChanged fieldSensitivityChanged:
                content.Apply(fieldSensitivityChanged, occurredAt);
                break;
            default:
                throw new InvalidOperationException(
                    $"{@event.GetType().Name} has no Content.Apply overload, so appending it would leave the "
                    + "document unchanged. Add the overload and a case here before emitting this event.");
        }
    }

    /// <summary>Is there a <c>Content.Apply</c> overload for this event?</summary>
    public static bool IsProjected(object @event) =>
        @event is ContentCreated or ContentUpdated or ContentStatusChanged
            or ContentScheduled or ContentSensitivityChanged or ContentTransitioned
            or ContentFieldSensitivityChanged;
}
