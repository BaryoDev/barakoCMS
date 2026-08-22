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
/// </remarks>
public interface IContentWriter
{
    /// <summary>
    /// Records the creation of content, starting its stream and its document.
    /// </summary>
    /// <returns>The document, so the caller can read back what was applied.</returns>
    Content Create(Events.ContentCreated @event);

    /// <summary>
    /// Records a change to existing content, appending to its stream and updating its document.
    /// </summary>
    /// <param name="content">The document to update, already loaded by the caller.</param>
    /// <param name="event">
    /// The event describing the change. It must have a matching <c>Content.Apply</c> overload, or
    /// this throws rather than appending an event the document will silently ignore.
    /// </param>
    void Append(Content content, object @event);

    /// <summary>
    /// Records several changes at once under an expected-version check, failing if the stream moved
    /// since it was read.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Append"/> because the concurrency guarantee differs and that
    /// difference is deliberate: an edit made against a stale read should be rejected rather than
    /// silently overwriting someone else's.
    /// </remarks>
    Task AppendOptimisticAsync(Content content, IReadOnlyList<object> events, CancellationToken cancellationToken);
}
