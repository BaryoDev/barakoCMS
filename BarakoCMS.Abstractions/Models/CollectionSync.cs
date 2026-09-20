namespace barakoCMS.Models;

/// <summary>Where a collection sync reads its entries from.</summary>
public enum SyncSource
{
    /// <summary>A <see cref="RequestDefinition"/> sent through its <see cref="Connector"/>, answering JSON.</summary>
    Request = 0,

    /// <summary>An RSS or Atom URL, fetched with a plain GET and no credentials.</summary>
    Feed = 1,
}

/// <summary>
/// A content type whose entries are filled from an outside source on a schedule, held as
/// configuration rather than as code.
/// </summary>
/// <remarks>
/// Three sites were each carrying the same shape of code: baryo.dev reads NuGet download counts and
/// GitHub repositories at build time, barakocms.com reads releases and milestones, rckoronadal shows
/// an RSS feed. Each was a file in a site repository, so the site could not be a deployment of the
/// same image with different data. See issues #794 and #722.
///
/// Nothing here is a secret and nothing here is a URL with a credential in it. A
/// <see cref="SyncSource.Request"/> sync names a request definition, which names a connector, which
/// is where the credential lives, encrypted and attached to the finished message by the sender. A
/// <see cref="SyncSource.Feed"/> sync sends no credential at all.
///
/// Entries are ordinary <see cref="Content"/>. Nothing marks them as synced, so blocks, delivery,
/// search and the admin treat them exactly as they treat anything else.
/// </remarks>
public class CollectionSync
{
    public Guid Id { get; set; }

    /// <summary>The admin's label, "NuGet downloads".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What the API addresses this by, "nuget-downloads". Unique per tenant.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>The content type whose entries this fills.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Whether the sweep runs this. A disabled sync leaves the entries it already wrote.</summary>
    public bool Enabled { get; set; } = true;

    public SyncSource Source { get; set; } = SyncSource.Request;

    /// <summary>For <see cref="SyncSource.Request"/>, the request definition to send.</summary>
    public string? RequestSlug { get; set; }

    /// <summary>For <see cref="SyncSource.Feed"/>, the absolute http or https URL of the feed.</summary>
    public string? FeedUrl { get; set; }

    /// <summary>
    /// The dotted path to the array of items in a JSON response, or empty when the response is
    /// itself the array.
    /// </summary>
    /// <remarks>
    /// The NuGet search API answers <c>{ "totalHits": 9, "data": [ ... ] }</c>, so this is "data".
    /// Ignored for a feed, where the items are the feed's own entries.
    /// </remarks>
    public string ItemsPath { get; set; } = string.Empty;

    /// <summary>
    /// Which source value becomes which content field, keyed by the content field's name.
    /// </summary>
    /// <remarks>
    /// The value is a dotted path into one item: "id", "totalDownloads", "authors[0]". For a feed it
    /// is one of the names <c>SyncPayloadReader</c> reads off an entry: title, link, id, summary,
    /// content, published, author, category.
    ///
    /// Keyed by the content field rather than by the source path, because two content fields may
    /// legitimately read the same source value and a source path may appear in a mapping only once
    /// if it is the key.
    /// </remarks>
    public Dictionary<string, string> FieldMap { get; set; } = new();

    /// <summary>
    /// The content field holding the stable key, so a re-sync updates an entry rather than adding
    /// another one.
    /// </summary>
    /// <remarks>
    /// Must be one of <see cref="FieldMap"/>'s keys. The entry's id is derived from the content type
    /// and this value, so the same key always addresses the same entry, however many times the sync
    /// runs and whatever order the source answers in.
    /// </remarks>
    public string KeyField { get; set; } = string.Empty;

    /// <summary>
    /// Fields kept at the greater of the stored and the fetched value.
    /// </summary>
    /// <remarks>
    /// A download count is the case this exists for. A registry index that is lagging answers a
    /// smaller number than it did an hour ago, and writing it walks the count backwards on a page
    /// that is meant to only ever go up. Each name must be one of <see cref="FieldMap"/>'s keys and a
    /// numeric field on the content type, since "greater" has to mean something.
    /// </remarks>
    public List<string> FloorFields { get; set; } = new();

    /// <summary>How long after a run the next one is due.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>How many items of one response are written. The rest are ignored.</summary>
    /// <remarks>
    /// A cap rather than a page size: nothing here follows a source's paging. A source that answers
    /// ten thousand items must not be able to turn one sweep into ten thousand writes, and a
    /// collection that wants more than <see cref="MaxEntriesCeiling"/> entries wants an import.
    /// </remarks>
    public int MaxEntries { get; set; } = 100;

    /// <summary>The status a written entry lands in.</summary>
    /// <remarks>
    /// Published by default, because a collection that is filled from outside exists to be served
    /// and nobody is going to review a hundred entries an hour. A sync into a type that needs
    /// review sets Draft.
    /// </remarks>
    public ContentStatus EntryStatus { get; set; } = ContentStatus.Published;

    /// <summary>When the sweep last attempted this, successfully or not.</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>When it last succeeded. A failed run leaves this where it was.</summary>
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>How many entries the last successful run wrote or refreshed.</summary>
    public int LastEntryCount { get; set; }

    /// <summary>
    /// Why the last run failed, or null when it succeeded.
    /// </summary>
    /// <remarks>
    /// Never a response body. A provider answering 401 frequently echoes the credential that was
    /// sent, which is why <c>ConnectorSender</c> refuses to carry one, and a failure written here is
    /// read back by an admin screen and stored for as long as the sync exists.
    /// </remarks>
    public string? LastError { get; set; }

    /// <summary>How many runs in a row have failed. Reset to zero by a success.</summary>
    public int ConsecutiveFailures { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The largest <see cref="MaxEntries"/> a sync may be saved with.</summary>
    public const int MaxEntriesCeiling = 500;

    /// <summary>The shortest <see cref="IntervalMinutes"/> a sync may be saved with.</summary>
    public const int MinIntervalMinutes = 5;

    /// <summary>Is this sync due at <paramref name="nowUtc"/>?</summary>
    public bool IsDue(DateTime nowUtc) =>
        Enabled && (LastRunAt is not { } last || last.AddMinutes(IntervalMinutes) <= nowUtc);
}
