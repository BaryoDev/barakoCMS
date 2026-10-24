using Marten;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Lets a module remove what it keeps about a content entry when that entry is erased.
/// </summary>
/// <remarks>
/// Called with the erasing session, before anything is committed, so what a handler queues goes in
/// the same transaction as the erasure. A handler queues deletes and does not call
/// <c>SaveChangesAsync</c>. Register it in DI as scoped.
/// </remarks>
public interface IContentErasureCascade
{
    Task QueueAsync(IDocumentSession session, Guid contentId, string contentType, CancellationToken ct);
}
