using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <param name="EventSourced">False when the type's policy says the document is the source of truth.</param>
/// <param name="Rebuilt">How many documents were produced from their streams.</param>
/// <param name="Skipped">
/// Items whose stream moved while the rebuild was folding it, left alone deliberately. The writer
/// that moved it stored the right document, so a rebuild is not needed for those and doing one
/// anyway would put an older fold over a newer one.
/// </param>
internal sealed record ContentRebuildResult(bool EventSourced, int Rebuilt, int Skipped = 0);

/// <summary>Discards the read model for an event-sourced type and produces it again from the streams.</summary>
internal interface IContentRebuilder
{
    Task<ContentRebuildResult> RebuildAsync(string contentTypeName, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// This is the column that decides whether the feature is real. If the document cannot be deleted
/// and rebuilt from the stream, the type is not event sourced whatever the flag says.
///
/// Refused for a type whose policy is not event sourced, and refused rather than made a no-op. Such
/// a type's document is the source of truth and its stream is an audit trail, so replaying the
/// stream over the document would be an overwrite dressed as a repair: anything written to the
/// document by a path that appends nothing would silently disappear.
/// </remarks>
internal sealed class ContentRebuilder : IContentRebuilder
{
    private readonly IDocumentSession _session;
    private readonly IContentSourcingPolicy _policy;

    public ContentRebuilder(IDocumentSession session, IContentSourcingPolicy policy)
    {
        _session = session;
        _policy = policy;
    }

    /// <inheritdoc />
    public async Task<ContentRebuildResult> RebuildAsync(string contentTypeName, CancellationToken cancellationToken)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(contentTypeName);

        if (!await _policy.IsEventSourcedAsync(name, cancellationToken))
        {
            return new ContentRebuildResult(false, 0);
        }

        // The session carries the tenant, and under conjoined event tenancy this query and the
        // stream fetches below are filtered by it. That is a security property rather than a
        // correctness one: a rebuild that crossed tenants would write one tenant's content into
        // another's documents, which is a breach and not a bug. Asserted with two tenants in
        // ContentSourcingTests rather than assumed of Marten.
        // Lowered on both sides rather than compared exactly. Events written before the create
        // endpoint normalised the name carry whatever the caller typed, so an exact match skips
        // every entry created as "Article" and reports a rebuild of zero. Normalising the writes
        // fixes the future; this is what reaches the past.
        var ids = await _session.Events.QueryRawEventDataOnly<ContentCreated>()
            .Where(e => e.ContentType.ToLower() == name)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        var rebuilt = 0;
        var skipped = 0;

        foreach (var id in ids.Distinct())
        {
            var stream = await _session.Events.FetchStreamAsync(id, token: cancellationToken);
            var folded = ContentProjection.Fold(stream);
            if (folded is null)
            {
                continue;
            }

            // Re-read, and skip the item if its stream moved while this one was being folded.
            //
            // Store is a blind upsert and Content carries no document concurrency, so a rebuild that
            // folded to version 5 while an editor committed version 6 wrote version 5 over the top
            // of them. The event was never lost, the stream is right, but the document regressed
            // until somebody happened to save that item again. There is no projection to notice.
            //
            // The editor's own write already stored the correct fold of version 6, so there is
            // nothing to repair here and skipping is the whole fix.
            var current = await _session.Events.FetchStreamStateAsync(id, cancellationToken);
            if (current is null || current.Version != stream.Count)
            {
                skipped++;
                continue;
            }

            _session.Store(folded);
            rebuilt++;

            // One item per commit, not two hundred. Batching meant the first stream of a batch waited
            // on 199 more fetches before its write landed, which made the window above as wide as the
            // batch rather than as wide as one fold.
            await _session.SaveChangesAsync(cancellationToken);
        }

        return new ContentRebuildResult(true, rebuilt, skipped);
    }
}
