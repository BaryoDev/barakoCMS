using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <param name="EventSourced">False when the type's policy says the document is the source of truth.</param>
/// <param name="Rebuilt">How many documents were produced from their streams.</param>
internal sealed record ContentRebuildResult(bool EventSourced, int Rebuilt);

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
    /// <summary>Streams per commit. A rebuild is an operation with a duration, not an instant.</summary>
    private const int BatchSize = 200;

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
        var ids = await _session.Events.QueryRawEventDataOnly<ContentCreated>()
            .Where(e => e.ContentType == name)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        var rebuilt = 0;
        foreach (var batch in ids.Distinct().Chunk(BatchSize))
        {
            foreach (var id in batch)
            {
                var stream = await _session.Events.FetchStreamAsync(id, token: cancellationToken);
                var folded = ContentProjection.Fold(stream);
                if (folded is null)
                {
                    continue;
                }

                _session.Store(folded);
                rebuilt++;
            }

            await _session.SaveChangesAsync(cancellationToken);
        }

        return new ContentRebuildResult(true, rebuilt);
    }
}
