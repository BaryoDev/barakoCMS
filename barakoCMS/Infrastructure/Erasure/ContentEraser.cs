using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Infrastructure.Erasure;

/// <summary>Removes a content item and its history, irrecoverably.</summary>
public interface IContentEraser
{
    /// <summary>
    /// Erases the item, returning false when it was not there. Throws when the deployment's mode has
    /// no erasure path.
    /// </summary>
    Task<bool> EraseAsync(Guid contentId, CancellationToken ct);
}

/// <summary>
/// Erasure by removing the stream. See DECISIONS.md D9 for why this is the default mode.
/// </summary>
/// <remarks>
/// This is a delete below Marten's API, which is deliberate rather than lazy.
/// <c>CompactStreamAsync</c> is the API that looks right and is not: it requires a registered
/// aggregation projection, which this project does not have for <c>Content</c> because the read
/// model is written by <c>IContentWriter</c> in the same transaction, and even with one it replaces
/// the events with a snapshot of current state, which is the data an erasure is removing.
/// <c>ArchiveStream</c> is softer still: it sets a flag and leaves every byte in place.
///
/// One transaction, in this order: events, stream, document. An interruption partway must not leave
/// a stream whose events are gone, because that reads as corruption rather than erasure.
///
/// Deleting rows leaves the remaining sequences monotonic, so the projection daemon's high-water
/// mark does not move and nothing is reprocessed or skipped. That is asserted by a test rather than
/// assumed, because a quietly stopped projection is a failure this project has already had.
/// </remarks>
public sealed class ContentEraser : IContentEraser
{
    private readonly IDocumentSession _session;
    private readonly ErasureOptions _options;
    private readonly ILogger<ContentEraser> _logger;

    public ContentEraser(IDocumentSession session, ErasureOptions options, ILogger<ContentEraser> logger)
    {
        _session = session;
        _options = options;
        _logger = logger;
    }

    public async Task<bool> EraseAsync(Guid contentId, CancellationToken ct)
    {
        if (_options.Mode == ErasureMode.None)
        {
            throw new InvalidOperationException(
                "This deployment runs Erasure:Mode=None, which has no erasure path.");
        }

        var content = await _session.LoadAsync<Models.Content>(contentId, ct);
        if (content is null)
        {
            return false;
        }

        // Queued on the session, not run on its connection. Marten already has a transaction open
        // there, so opening another throws, and more importantly this way the three deletes and the
        // document removal commit together: an interruption partway must not leave a stream whose
        // events are gone, because that reads as corruption rather than erasure.
        //
        // One statement, as a data-modifying CTE, and both alternatives are ruled out rather than
        // unconsidered. mt_events has a foreign key to mt_streams, so the events must go first, and
        // Marten does not guarantee the order of queued commands against each other or against
        // document operations: queued separately, this passed in isolation and failed in the full
        // suite. Marten also refuses a command containing ';', so the two deletes cannot be
        // sequenced in one string either.
        //
        // The CTE gives both properties: one statement, events removed before the stream row, and
        // the foreign key checked once at the end of it.
        //
        // Marten's placeholder is ?, not a named parameter, and each occurrence takes its own value.
        _session.QueueSqlCommand(
            "with erased_events as (delete from public.mt_events where stream_id = ? returning stream_id) "
            + "delete from public.mt_streams where id = ?",
            contentId, contentId);
        _session.Delete(content);

        await _session.SaveChangesAsync(ct);

        // The id and the row count, never the content. A log line about an erasure that quotes what
        // was erased is not an erasure.
        _logger.LogInformation(
            "Erased content {ContentId}: events, stream and document removed", contentId);

        return true;
    }
}
