namespace barakoCMS.Core.Interfaces;

/// <summary>Fires a trigger a module registered, starting the workflows that listen for it.</summary>
/// <remarks>
/// Resolve it from the request's scope. It starts runs in that scope's tenant and nowhere else.
/// </remarks>
public interface IWorkflowTriggers
{
    /// <summary>
    /// Queues a run of every switched-on workflow that listens for <paramref name="trigger"/> on the
    /// entry's content type and whose conditions the entry meets.
    /// </summary>
    /// <param name="trigger">
    /// A name the calling module registered with <see cref="barakoCMS.Modules.IModuleWorkflowTriggers"/>.
    /// Core's own triggers (Created, Published and the rest) and transitions are refused: core fires
    /// those from the event stream, and a module firing one would start runs for something that did
    /// not happen.
    /// </param>
    /// <param name="content">
    /// A stored entry. The runner loads it again by id when each action runs, so an entry that is
    /// not stored, or is gone by then, has its actions skipped.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many runs were queued.</returns>
    /// <remarks>
    /// The runs are committed before this returns, through the scope's <c>IDocumentSession</c>. Any
    /// change the caller left pending on that same session is committed with them, so save your
    /// own work first or accept that it goes in the same transaction.
    /// </remarks>
    /// <exception cref="ArgumentException">The trigger is not one a module registered, or the entry has no id or content type.</exception>
    Task<int> FireAsync(string trigger, barakoCMS.Models.Content content, CancellationToken ct);

    /// <summary>
    /// As <see cref="FireAsync(string, barakoCMS.Models.Content, CancellationToken)"/>, but a workflow
    /// that already has a run for this entry fired with the same <paramref name="triggerKey"/> is
    /// skipped, so a retried call starts the work once. A null key queues a run every time.
    /// </summary>
    /// <param name="trigger">A name the calling module registered.</param>
    /// <param name="content">A stored entry.</param>
    /// <param name="triggerKey">What makes this fire the same as an earlier one, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many runs were queued.</returns>
    /// <exception cref="NotSupportedException">An implementation written before keys existed was given a key.</exception>
    Task<int> FireAsync(string trigger, barakoCMS.Models.Content content, string? triggerKey, CancellationToken ct) =>
        triggerKey is null
            ? FireAsync(trigger, content, ct)
            : throw new NotSupportedException("This IWorkflowTriggers does not dedupe by trigger key.");
}
