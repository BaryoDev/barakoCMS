using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <inheritdoc />
public sealed class ContentWriter : IContentWriter
{
    private readonly IDocumentSession _session;
    private readonly IContentSourcingPolicy _policy;

    /// <summary>Streams this session has already staged writes for.</summary>
    /// <remarks>
    /// A refresh from what is committed would undo them: what is in the database is the state before
    /// this session's own uncommitted work. For those the caller's copy is the current one.
    /// </remarks>
    private readonly HashSet<Guid> _staged = new();

    public ContentWriter(IDocumentSession session, IContentSourcingPolicy policy)
    {
        _session = session;
        _policy = policy;
    }

    /// <inheritdoc />
    [Obsolete("Use CreateAsync. Removal planned for barakoCMS 5.0.")]
    public Content Create(ContentCreated @event) => CreateCore(@event);

    /// <inheritdoc />
    public Task<Content> CreateAsync(ContentCreated @event, CancellationToken cancellationToken)
    {
        // No branch, and that is not an omission. A brand new stream holds exactly one event, so
        // folding it and applying it to a blank document are the same operation and produce the same
        // bytes. The two modes diverge from the second event onwards, which is where the document
        // either keeps its own drift or is discarded in favour of the stream.
        return Task.FromResult(CreateCore(@event));
    }

    private Content CreateCore(ContentCreated @event)
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
    [Obsolete("Use AppendAsync. Removal planned for barakoCMS 5.0.")]
    public void Append(Content content, object @event)
    {
        ApplyToDocument(content, @event);

        _session.Events.Append(content.Id, @event);
        _session.Store(content);
        _staged.Add(content.Id);
    }

    /// <inheritdoc />
    public async Task AppendAsync(Content content, object @event, CancellationToken cancellationToken)
    {
        // Before anything is staged, so an event with no projection cannot leave half a write behind.
        AssertHasProjection(@event);

        // Event sourced: the caller's copy is discarded and rebuilt from the stream, so anything
        // that reached the document by some other route is gone. Document mode: the caller's copy is
        // the record and the change is applied on top of it, which is what every type did before
        // this existed.
        await RebuildFromStreamAsync(content, cancellationToken);

        _session.Events.Append(content.Id, @event);
        ApplyToDocument(content, @event);

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
        if (!await RebuildFromStreamAsync(content, cancellationToken) && _staged.Add(content.Id))
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

    /// <inheritdoc />
    public async Task AppendAsync(
        Content content,
        IReadOnlyList<object> events,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (await _policy.IsEventSourcedAsync(content.ContentType, cancellationToken))
        {
            // Checked here rather than left to AppendOptimistic. That one guards the window from the
            // append to the commit and nothing before it, so an edit made against a read taken
            // minutes ago commits cleanly and silently overwrites everything in between. For an
            // event-sourced type the stream is the record, so a write that does not know where the
            // stream was is refused instead.
            var state = await _session.Events.FetchStreamStateAsync(content.Id, cancellationToken);
            var actual = state?.Version ?? 0;

            if (expectedVersion is null || expectedVersion != actual)
            {
                throw new StaleContentException(content.Id, expectedVersion, actual);
            }
        }

        await AppendOptimisticAsync(content, events, cancellationToken);
    }

    /// <summary>
    /// For an event-sourced type, replaces <paramref name="content"/> with the fold of its committed
    /// stream.
    /// </summary>
    /// <returns>True when the document was rebuilt, false when the caller's copy stands.</returns>
    /// <remarks>
    /// This is what "the stream is the source of truth" means in practice, and it is the observable
    /// difference between the two modes: a value that reached the document by any route other than
    /// an event does not survive the next write, because the fold never saw an event carrying it.
    ///
    /// Skipped when this session already staged writes to the stream, for the same reason the
    /// document refresh is: the committed stream does not yet include this session's own appends,
    /// so folding it would undo them.
    /// </remarks>
    private async Task<bool> RebuildFromStreamAsync(Content content, CancellationToken cancellationToken)
    {
        if (_staged.Contains(content.Id))
        {
            return false;
        }

        if (!await _policy.IsEventSourcedAsync(content.ContentType, cancellationToken))
        {
            return false;
        }

        var stream = await _session.Events.FetchStreamAsync(content.Id, token: cancellationToken);
        var folded = ContentProjection.Fold(stream);
        if (folded is null)
        {
            return false;
        }

        CopyState(folded, content);
        _staged.Add(content.Id);
        return true;
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
        if (ContentProjection.IsProjected(@event))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{@event.GetType().Name} has no Content.Apply overload, so appending it would leave the "
            + "document unchanged. Add the overload and a case in ContentProjection before emitting it.");
    }

    /// <summary>
    /// Applies an event to the document as the change happens.
    /// </summary>
    /// <remarks>
    /// <c>DateTime.UtcNow</c> is correct here because this is the moment the change happens. A
    /// rebuild replaying old events passes the event's own timestamp instead, which is why
    /// <c>Apply</c> takes it rather than reading the clock itself.
    /// </remarks>
    private static void ApplyToDocument(Content content, object @event)
        => ContentProjection.Apply(content, @event, DateTime.UtcNow);
}
