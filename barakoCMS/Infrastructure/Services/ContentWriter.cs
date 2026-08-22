using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <inheritdoc />
public sealed class ContentWriter : IContentWriter
{
    private readonly IDocumentSession _session;

    public ContentWriter(IDocumentSession session) => _session = session;

    /// <inheritdoc />
    public Content Create(ContentCreated @event)
    {
        var content = new Content();
        ApplyToDocument(content, @event);

        // The stream and the document are staged together so a partial failure cannot leave one
        // without the other.
        _session.Events.StartStream<Content>(@event.Id, @event);
        _session.Store(content);

        return content;
    }

    /// <inheritdoc />
    public void Append(Content content, object @event)
    {
        ApplyToDocument(content, @event);

        _session.Events.Append(content.Id, @event);
        _session.Store(content);
    }

    /// <inheritdoc />
    public async Task AppendOptimisticAsync(Content content, IReadOnlyList<object> events, CancellationToken cancellationToken)
    {
        // Checked before anything is staged. AppendOptimistic queues the events onto the session, so
        // rejecting the third of five afterwards leaves the first two staged: a caller that catches
        // and commits anyway writes events with no matching change to the document, which is the
        // exact divergence this class exists to prevent.
        foreach (var @event in events)
        {
            AssertHasProjection(@event);
        }

        await _session.Events.AppendOptimistic(content.Id, cancellationToken, events.ToArray());

        foreach (var @event in events)
        {
            ApplyToDocument(content, @event);
        }

        _session.Store(content);
    }

    private static void AssertHasProjection(object @event)
    {
        if (@event is ContentCreated or ContentUpdated or ContentStatusChanged
            or ContentScheduled or ContentSensitivityChanged)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{@event.GetType().Name} has no Content.Apply overload, so appending it would leave the "
            + "document unchanged. Add the overload and a case in ApplyToDocument before emitting it.");
    }

    /// <summary>
    /// Routes an event to the matching <c>Content.Apply</c> overload.
    /// </summary>
    /// <remarks>
    /// The unmatched case throws rather than doing nothing. An event with no projection would append
    /// cleanly and leave the document unchanged, which reads as a successful save and is only
    /// visible later as a document that disagrees with its own history. Failing the write is the
    /// louder and cheaper outcome.
    ///
    /// <c>DateTime.UtcNow</c> is correct here because this is the moment the change happens. A
    /// rebuild replaying old events must pass the event's own timestamp instead, which is why
    /// <c>Apply</c> takes it rather than reading the clock itself.
    /// </remarks>
    private static void ApplyToDocument(Content content, object @event)
    {
        var occurredAt = DateTime.UtcNow;

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
            case ContentSensitivityChanged sensitivityChanged:
                content.Apply(sensitivityChanged, occurredAt);
                break;
            default:
                throw new InvalidOperationException(
                    $"{@event.GetType().Name} has no Content.Apply overload, so appending it would leave the "
                    + "document unchanged. Add the overload and a case here before emitting this event.");
        }
    }
}
