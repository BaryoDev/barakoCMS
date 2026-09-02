using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Services;

/// <inheritdoc />
public sealed class ContentWriter : IContentWriter
{
    /// <summary>Whether a type that is not event sourced still writes its changes to a stream.</summary>
    /// <remarks>
    /// True by default and by omission, which is what every deployment does today: a document type
    /// keeps appending, so <c>GET /api/contents/{id}/history</c>, the rollback endpoint and the audit
    /// trail go on working for it.
    ///
    /// Setting it false is the behaviour issue #331 asked for, and it is not free. Workflows are
    /// triggered by <c>WorkflowProjection</c> reading committed ContentCreated, ContentUpdated and
    /// ContentStatusChanged events, so with no events there are no workflow runs for that type. The
    /// history endpoint returns nothing for it and a rollback has nothing to roll back to. Anyone
    /// turning it off is choosing that, and the documentation says so in those words.
    /// </remarks>
    public const string DocumentTypesAppendKey = "EventSourcing:DocumentTypesAppend";

    private readonly IDocumentSession _session;
    private readonly IContentSourcingPolicy _policy;
    private readonly bool _documentTypesAppend;

    /// <summary>Streams this session has already staged writes for.</summary>
    /// <remarks>
    /// A refresh from what is committed would undo them: what is in the database is the state before
    /// this session's own uncommitted work. For those the caller's copy is the current one.
    /// </remarks>
    private readonly HashSet<Guid> _staged = new();

    /// <summary>Writes with document types appending, which is the default and the old behaviour.</summary>
    public ContentWriter(IDocumentSession session, IContentSourcingPolicy policy)
        : this(session, policy, documentTypesAppend: true)
    {
    }

    public ContentWriter(IDocumentSession session, IContentSourcingPolicy policy, IConfiguration configuration)
        : this(session, policy, configuration.GetValue(DocumentTypesAppendKey, true))
    {
    }

    private ContentWriter(IDocumentSession session, IContentSourcingPolicy policy, bool documentTypesAppend)
    {
        _session = session;
        _policy = policy;
        _documentTypesAppend = documentTypesAppend;
    }

    /// <inheritdoc />
    [Obsolete("Use CreateAsync. Removal planned for barakoCMS 5.0.")]
    public Content Create(ContentCreated @event) => CreateCore(@event);

    /// <inheritdoc />
    public async Task<Content> CreateAsync(ContentCreated @event, CancellationToken cancellationToken)
    {
        // The fold is not branched on, and that is not an omission. A brand new stream holds exactly
        // one event, so folding it and applying it to a blank document are the same operation and
        // produce the same bytes. The two modes diverge from the second event onwards, which is
        // where the document either keeps its own drift or is discarded in favour of the stream.
        //
        // Whether a stream is started at all is branched on, because that is what the flag decides.
        var append = _documentTypesAppend
            || await _policy.IsEventSourcedAsync(@event.ContentType, cancellationToken);

        return CreateCore(@event, append);
    }

    private Content CreateCore(ContentCreated @event, bool append = true)
    {
        var content = new Content();
        ApplyToDocument(content, @event);

        if (append)
        {
            // The stream and the document are staged together so a partial failure cannot leave one
            // without the other.
            _session.Events.StartStream<Content>(@event.Id, @event);
        }

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

        if (await ShouldAppendAsync(content.ContentType, cancellationToken))
        {
            _session.Events.Append(content.Id, @event);
        }

        ApplyToDocument(content, @event);

        _session.Store(content);
        _staged.Add(content.Id);
    }

    /// <summary>Does a change to this type get written to a stream?</summary>
    /// <remarks>
    /// Always, for an event-sourced type: the stream is its record and there is nothing to configure.
    /// For every other type it is <see cref="DocumentTypesAppendKey"/>, which is true unless a
    /// deployment turned it off.
    /// </remarks>
    private async Task<bool> ShouldAppendAsync(string contentType, CancellationToken cancellationToken)
        => _documentTypesAppend || await _policy.IsEventSourcedAsync(contentType, cancellationToken);

    /// <inheritdoc />
    public async Task AppendOptimisticAsync(Content content, IReadOnlyList<object> events, CancellationToken cancellationToken)
        => await AppendCoreAsync(
            content, events, expectedVersion: null,
            await ShouldAppendAsync(content.ContentType, cancellationToken), cancellationToken);

    /// <summary>
    /// Appends and rebuilds, optionally binding the version the caller read at to the append itself.
    /// </summary>
    /// <param name="expectedVersion">
    /// The stream version the caller's copy was read at, or null to use Marten's optimistic append.
    /// </param>
    /// <remarks>
    /// When a version is given it is bound to the append rather than compared beforehand, so Postgres
    /// enforces it when the commit lands. Fetching the state and comparing it in C# narrows the
    /// window and does not close it: another writer can append between the read and the append, and
    /// the check passes on a stream that has already moved. A check that can be overtaken is not a
    /// concurrency control, it is a smaller race.
    ///
    /// Marten's expected version is where the stream ends up, so it is the caller's version plus the
    /// events being written.
    /// </remarks>
    private async Task AppendCoreAsync(
        Content content, IReadOnlyList<object> events, long? expectedVersion, bool append, CancellationToken cancellationToken)
    {
        // Checked before anything is staged. AppendOptimistic queues the events onto the session, so
        // rejecting the third of five afterwards leaves the first two staged: a caller that catches
        // and commits anyway writes events with no matching change to the document, which is the
        // exact divergence this class exists to prevent.
        foreach (var @event in events)
        {
            AssertHasProjection(@event);
        }

        if (!append)
        {
            // Nothing is written to a stream, so there is nothing to guard and no stream to fold
            // from. The document below is the whole of the record for this type.
        }
        else if (expectedVersion is { } version)
        {
            _session.Events.Append(content.Id, version + events.Count, events.ToArray());
        }
        else
        {
            await _session.Events.AppendOptimistic(content.Id, cancellationToken, events.ToArray());
        }

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
        if (!await _policy.IsEventSourcedAsync(content.ContentType, cancellationToken))
        {
            // Document mode keeps last-write-wins, which is what every type did before this existed.
            await AppendCoreAsync(content, events, expectedVersion: null, _documentTypesAppend, cancellationToken);
            return;
        }

        // Compared here for the message, not for the guarantee. Knowing both the expected and the
        // actual version is what lets the endpoint answer 409 with something a client can act on,
        // and a mismatch this obvious is worth refusing before anything is staged.
        var state = await _session.Events.FetchStreamStateAsync(content.Id, cancellationToken);
        var actual = state?.Version ?? 0;

        if (expectedVersion is null || expectedVersion != actual)
        {
            // Null included. For an event-sourced type the stream is the record, so "I did not check"
            // is not a thing a writer may say.
            throw new StaleContentException(content.Id, expectedVersion, actual);
        }

        // The guarantee is this, not the comparison above. That comparison narrows the window and
        // cannot close it: another writer can commit between the fetch and the append, and the
        // comparison then passes on a stream that has already moved. Binding the caller's version
        // into the append makes Postgres refuse the commit instead, which is not something a
        // scheduler running at the same moment can slip past.
        //
        // Nothing is caught here on purpose. The append only registers the expectation; the refusal
        // arrives from SaveChangesAsync, which is the caller's call, so a catch around this line
        // would be a handler for an exception that cannot reach it. The Update endpoint already
        // catches the concurrency exception off its own SaveChangesAsync and answers 412.
        await AppendCoreAsync(content, events, expectedVersion, append: true, cancellationToken);
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
