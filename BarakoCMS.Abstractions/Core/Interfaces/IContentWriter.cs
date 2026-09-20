using barakoCMS.Models;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// The single way content is recorded.
/// </summary>
/// <remarks>
/// Six slices used to write content, and each held its own copy of what that means: append an event,
/// apply it to the document, store the document. They had already drifted, and two of them appended
/// nothing at all, so there was content with no history behind it.
///
/// A caller here says what happened. It does not decide how that is persisted. Both operations stage
/// into the current session and do not commit: the calling slice still owns its transaction
/// boundary, because several of them do more work in the same unit.
///
/// This is also where a content type's sourcing policy is read, and the only place. An event-sourced
/// type has its document produced by folding the stream; every other type keeps the document it has
/// always had, written straight from the change. Both modes append the same events, so history and
/// the audit trail are unchanged for a type that opts out.
/// </remarks>
public interface IContentWriter
{
    /// <summary>
    /// Records the creation of content, starting its stream and its document.
    /// </summary>
    /// <returns>The document, so the caller can read back what was applied.</returns>
    [Obsolete("Use CreateAsync, which can read the content type's sourcing policy. This one cannot, "
        + "so it always takes the document path. Removal planned for barakoCMS 5.0.")]
    Content Create(Events.ContentCreated @event);

    /// <summary>
    /// Records a change to existing content, appending to its stream and updating its document.
    /// </summary>
    /// <param name="content">The document to update, already loaded by the caller.</param>
    /// <param name="event">
    /// The event describing the change. It must have a matching <c>Content.Apply</c> overload, or
    /// this throws rather than appending an event the document will silently ignore.
    /// </param>
    [Obsolete("Use AppendAsync, which can read the content type's sourcing policy. This one cannot, "
        + "so it always takes the document path. Removal planned for barakoCMS 5.0.")]
    void Append(Content content, object @event);

    /// <summary>
    /// Records several changes at once under an expected-version check, failing if the stream moved
    /// since it was read.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Append"/> because the concurrency guarantee differs and that
    /// difference is deliberate: an edit made against a stale read should be rejected rather than
    /// silently overwriting someone else's.
    ///
    /// The document is refreshed before the events are applied, so what is stored reflects
    /// everything already recorded and not just the caller's load. Expect <paramref name="content"/>
    /// to come back carrying changes the caller never made.
    /// </remarks>
    Task AppendOptimisticAsync(Content content, IReadOnlyList<object> events, CancellationToken cancellationToken);

    /// <summary>
    /// Records the creation of content, honouring the content type's sourcing policy.
    /// </summary>
    /// <returns>The document, so the caller can read back what was applied.</returns>
    /// <remarks>
    /// The default implementation delegates to the older synchronous member so an implementor
    /// written before the policy still compiles. It does <b>not</b> consult the policy, because an
    /// interface default has no way to read it. Override this to get event-sourced behaviour.
    /// </remarks>
    Task<Content> CreateAsync(Events.ContentCreated @event, CancellationToken cancellationToken)
    {
#pragma warning disable CS0618 // the default exists so an implementor written before the policy still compiles
        return Task.FromResult(Create(@event));
#pragma warning restore CS0618
    }

    /// <summary>
    /// Records a change to existing content, honouring the content type's sourcing policy.
    /// </summary>
    /// <remarks>
    /// As with <see cref="CreateAsync"/>, the default delegates to the older synchronous member and
    /// does not consult the policy. Override it to get event-sourced behaviour.
    /// </remarks>
    Task AppendAsync(Content content, object @event, CancellationToken cancellationToken)
    {
#pragma warning disable CS0618
        Append(content, @event);
#pragma warning restore CS0618
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records several changes under the concurrency rule the content type's sourcing policy sets.
    /// </summary>
    /// <param name="expectedVersion">
    /// The stream version the caller's copy was read at, or null when the caller did not check.
    /// </param>
    /// <remarks>
    /// An event-sourced type refuses both a stale value and a missing one, with
    /// <see cref="StaleContentException"/>. Every other type keeps last-write-wins and behaves
    /// exactly as <see cref="AppendOptimisticAsync"/> does, which is what it did before this
    /// existed.
    ///
    /// There is no default. The other two members above can delegate to their older synchronous
    /// forms and be merely incomplete, but this one takes a concurrency token: a default that
    /// forwarded to <see cref="AppendOptimisticAsync"/> would discard <paramref name="expectedVersion"/>
    /// and hand back last-write-wins while the caller believed a stale write had been refused. That
    /// is the failure this member exists to prevent, so an implementor has to write it.
    /// </remarks>
    Task AppendAsync(Content content, IReadOnlyList<object> events, long? expectedVersion, CancellationToken cancellationToken);
}
