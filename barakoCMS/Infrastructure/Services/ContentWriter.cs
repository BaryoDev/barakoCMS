using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <inheritdoc />
public sealed class ContentWriter : IContentWriter
{
    private readonly IDocumentSession _session;

    /// <summary>Streams this session has already staged writes for.</summary>
    /// <remarks>
    /// A refresh from the committed document would undo them: what is in the database is the state
    /// before this session's own uncommitted work. For those the caller's copy is the current one.
    /// </remarks>
    private readonly HashSet<Guid> _staged = new();

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
        _staged.Add(@event.Id);

        return content;
    }

    /// <inheritdoc />
    public void Append(Content content, object @event)
    {
        ApplyToDocument(content, @event);

        _session.Events.Append(content.Id, @event);
        _session.Store(content);
        _staged.Add(content.Id);
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

        // The document is rebuilt on top of what is committed now, not on top of the caller's load.
        //
        // AppendOptimistic guards the stream from here to the commit, and nothing at all before it.
        // The caller's copy was read at the start of the request, so a writer that committed in
        // between is invisible to it: the scheduler publishes a due draft at v6, this request loaded
        // v5 where the status was Draft, and storing that snapshot alongside a ContentUpdated at v7
        // silently un-publishes the item with no event recording it. The stream then says Published
        // and the read model says Draft, permanently, and delivery stops serving it.
        //
        // Reading here rather than before the append is what makes it safe: from the append onwards
        // any further write to this stream fails this commit, so what is read now is still true when
        // it is stored.
        if (_staged.Add(content.Id))
        {
            var committed = await _session.LoadAsync<Content>(content.Id, cancellationToken);
            if (committed is not null)
            {
                CopyState(committed, content);
            }
        }

        foreach (var @event in events)
        {
            ApplyToDocument(content, @event);
        }

        _session.Store(content);
    }

    private static readonly System.Reflection.PropertyInfo[] ContentState = typeof(Content)
        .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite)
        .ToArray();

    /// <summary>
    /// Overwrites <paramref name="target"/> with the state of <paramref name="source"/>, in place.
    /// </summary>
    /// <remarks>
    /// In place because the caller holds the reference and reads it back after the write returns.
    /// Reflected over rather than assigned field by field on purpose: a hand-written list silently
    /// stops copying a property the day one is added, and the symptom would be that one field
    /// reverting under concurrency, which is precisely the bug this exists to prevent.
    /// </remarks>
    private static void CopyState(Content source, Content target)
    {
        foreach (var property in ContentState)
        {
            property.SetValue(target, property.GetValue(source));
        }
    }

    private static void AssertHasProjection(object @event)
    {
        if (@event is ContentCreated or ContentUpdated or ContentStatusChanged
            or ContentScheduled or ContentSensitivityChanged or ContentTransitioned)
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
            case ContentTransitioned transitioned:
                content.Apply(transitioned, occurredAt);
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
