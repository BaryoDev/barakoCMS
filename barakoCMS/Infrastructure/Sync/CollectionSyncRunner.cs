using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Sync;

/// <summary>
/// What one run of a collection sync did. Excluded counts items an exclude rule skipped, which
/// unlike Skipped is not a fault. Archived counts entries archived because the run no longer
/// produced them.
/// </summary>
internal sealed record CollectionSyncOutcome(
    bool Succeeded, int Created, int Updated, int Unchanged, int Skipped, string? Error, int Excluded = 0)
{
    public static CollectionSyncOutcome Failed(string error) => new(false, 0, 0, 0, 0, error);

    public int Archived { get; init; }

    /// <summary>The keys of the entries this run created, updated or left unchanged.</summary>
    public IReadOnlySet<string> Produced { get; init; } = new HashSet<string>();

    /// <summary>
    /// Whether the run read everything the source holds: at least one item, no <c>MaxEntries</c>
    /// cut, no next page, and no item skipped, whose key would be unknown. An empty answer counts as
    /// incomplete, since a provider having a bad moment answers with an empty list too.
    /// </summary>
    public bool ReadAll { get; init; }

    /// <summary>Entries this run left standing, whether or not it had to write them.</summary>
    public int Entries => Created + Updated + Unchanged;
}

internal interface ICollectionSyncRunner
{
    /// <summary>
    /// Fetches the source, writes the entries, and records the outcome on the sync itself.
    /// </summary>
    /// <remarks>
    /// The session comes from the scope this is resolved in, so the caller decides which tenant is
    /// being filled by choosing the scope. A sweep opens one per partition.
    /// </remarks>
    Task<CollectionSyncOutcome> RunAsync(CollectionSync sync, CancellationToken ct);
}

/// <summary>
/// Fills a content type's entries from an outside source, once.
/// </summary>
/// <remarks>
/// The outbound half of this is entirely the existing connector path: a request definition composed
/// by <see cref="IRequestComposer"/> and sent by <see cref="IConnectorFetcher"/>, which is the same
/// class, the same HTTP client, the same address guard and the same credential handling that a
/// workflow's Request action uses. Nothing here opens a socket of its own.
///
/// The inbound half writes ordinary <see cref="Content"/> through <see cref="IContentWriter"/>, so a
/// synced entry has a stream, a history and a workflow trigger exactly like an entry somebody typed.
/// </remarks>
internal sealed class CollectionSyncRunner(
    IDocumentSession session,
    IRequestComposer composer,
    IConnectorFetcher fetcher,
    IContentWriter writer,
    ILogger<CollectionSyncRunner> logger) : ICollectionSyncRunner
{
    /// <summary>The largest response body a sync will read.</summary>
    /// <remarks>
    /// Two megabytes is several times the largest of the sources this was written for (a NuGet
    /// search page, a GitHub releases page, a blog feed) and small enough that a provider answering
    /// something unbounded cannot be the reason the sweep runs out of memory.
    /// </remarks>
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    /// <summary>The actor a synced write is attributed to. Not a user, because no user did it.</summary>
    public static readonly Guid SystemActor = Guid.Empty;

    public async Task<CollectionSyncOutcome> RunAsync(CollectionSync sync, CancellationToken ct)
    {
        var outcome = await ApplyAsync(sync, ct);

        if (outcome.Succeeded && sync.ArchiveMissing)
        {
            outcome = outcome with { Archived = await ArchiveMissingAsync(sync, outcome.Produced, outcome.ReadAll, ct) };
        }

        sync.LastRunAt = DateTime.UtcNow;
        sync.UpdatedAt = sync.LastRunAt.Value;

        if (outcome.Succeeded)
        {
            sync.LastSuccessAt = sync.LastRunAt;
            sync.LastEntryCount = outcome.Entries;
            sync.LastError = null;
            sync.ConsecutiveFailures = 0;
        }
        else
        {
            // The entries are not touched, and LastSuccessAt and LastEntryCount are left where the
            // last good run put them. That pair is what tells an operator the page they are looking
            // at is real but stale, rather than empty.
            sync.LastError = outcome.Error;
            sync.ConsecutiveFailures++;

            logger.LogWarning(
                "Collection sync {Slug} failed ({Failures} in a row): {Reason}",
                sync.Slug, sync.ConsecutiveFailures, outcome.Error);
        }

        session.Store(sync);
        await session.SaveChangesAsync(ct);

        return outcome;
    }

    /// <summary>
    /// Archives the published entries this sync owns that a complete run did not produce, and
    /// records what it owns now.
    /// </summary>
    /// <returns>How many entries were archived.</returns>
    /// <remarks>
    /// A run that is not complete archives nothing and only adds to what the sync owns: an item it
    /// did not read may still be at the source. A skipped item counts as incomplete too, since its
    /// key is unknown and its entry would otherwise look missing.
    ///
    /// Nothing is saved here. The archives are staged and committed with the sync document by
    /// <see cref="RunAsync"/>, so an entry is never archived without its key reaching
    /// <see cref="CollectionSync.ArchivedKeys"/>, which is what lets it be published again.
    ///
    /// Archived rather than erased, through the same ContentStatusChanged the status endpoint and the
    /// schedule append, so history and workflows see it. Only a Published entry is archived: a draft
    /// or an entry somebody already archived is left as it is.
    /// </remarks>
    private async Task<int> ArchiveMissingAsync(
        CollectionSync sync, IReadOnlySet<string> produced, bool complete, CancellationToken ct)
    {
        var archivedKeys = sync.ArchivedKeys.Where(k => !produced.Contains(k)).ToList();

        if (!complete)
        {
            sync.SyncedKeys = sync.SyncedKeys
                .Where(k => !produced.Contains(k))
                .Concat(produced.Order(StringComparer.Ordinal))
                .TakeLast(CollectionSync.MaxSyncedKeys)
                .ToList();
            sync.ArchivedKeys = archivedKeys;
            return 0;
        }

        var archived = 0;

        foreach (var key in sync.SyncedKeys.Where(k => !produced.Contains(k)))
        {
            var entry = await session.LoadAsync<Content>(EntryId(sync.ContentType, key), ct);
            if (entry is not { Status: ContentStatus.Published }) continue;

            await writer.AppendAsync(
                entry, new ContentStatusChanged(entry.Id, ContentStatus.Archived, SystemActor, DateTime.UtcNow), ct);

            archivedKeys.Add(key);
            archived++;
        }

        sync.SyncedKeys = produced.Order(StringComparer.Ordinal).ToList();
        sync.ArchivedKeys = archivedKeys.TakeLast(CollectionSync.MaxArchivedKeys).ToList();

        return archived;
    }

    private async Task<CollectionSyncOutcome> ApplyAsync(CollectionSync sync, CancellationToken ct)
    {
        var schema = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == sync.ContentType, ct);

        if (schema is null)
        {
            return CollectionSyncOutcome.Failed(
                $"Content type '{sync.ContentType}' no longer exists, so there is nowhere to write entries.");
        }

        var fetched = await FetchAsync(sync, ct);
        if (fetched.Error is not null)
        {
            return CollectionSyncOutcome.Failed(fetched.Error);
        }

        var payload = sync.Source == SyncSource.Feed
            ? SyncPayloadReader.ReadFeed(fetched.Body!, sync.MaxEntries)
            : SyncPayloadReader.ReadJson(fetched.Body!, sync.ItemsPath, sync.MaxEntries, MappedPaths(sync));

        if (!payload.Ok)
        {
            return CollectionSyncOutcome.Failed(payload.Error!);
        }

        var created = 0;
        var updated = 0;
        var produced = new HashSet<string>(StringComparer.Ordinal);
        var unchanged = 0;
        var skipped = 0;
        string? firstSkip = null;

        foreach (var row in payload.Rows)
        {
            ct.ThrowIfCancellationRequested();

            if (SyncRules.Excluded(sync.Exclude, row)) continue;

            var mapped = Map(sync, schema, row, out var key, out var reason);
            if (mapped is null || string.IsNullOrWhiteSpace(key))
            {
                skipped++;
                firstSkip ??= reason;
                continue;
            }

            var id = EntryId(sync.ContentType, key);
            var existing = await session.LoadAsync<Content>(id, ct);
            produced.Add(key);

            if (existing is null)
            {
                await writer.CreateAsync(
                    new ContentCreated(
                        id, sync.ContentType, mapped, sync.EntryStatus, SystemActor,
                        SearchText(mapped, schema), SensitivityLevel.Public, DateTime.UtcNow),
                    ct);

                await session.SaveChangesAsync(ct);
                created++;
                continue;
            }

            // Started from what is stored, so a field an editor filled in by hand and the mapping
            // does not name survives the next sync. Only mapped fields are overwritten.
            var merged = new Dictionary<string, object>(existing.Data, StringComparer.Ordinal);
            foreach (var (field, value) in mapped)
            {
                merged[field] = Floor(sync, field, existing.Data, value);
            }

            var changed = Changed(existing.Data, merged);
            var restore = sync.ArchiveMissing
                && existing.Status == ContentStatus.Archived
                && sync.ArchivedKeys.Contains(key, StringComparer.Ordinal);

            if (!changed && !restore)
            {
                unchanged++;
                continue;
            }

            if (changed)
            {
                await writer.AppendAsync(
                    existing,
                    new ContentUpdated(id, merged, SystemActor, SearchText(merged, schema), DateTime.UtcNow),
                    ct);
            }

            if (restore)
            {
                await writer.AppendAsync(
                    existing, new ContentStatusChanged(id, sync.EntryStatus, SystemActor, DateTime.UtcNow), ct);
            }

            await session.SaveChangesAsync(ct);
            updated++;
        }

        if (created + updated + unchanged == 0 && skipped > 0)
        {
            // Every item was unusable, which is a broken mapping rather than a quiet run. Reported
            // as a failure so it shows up where an operator looks, and so the entries from the last
            // good run are what the site keeps serving.
            return CollectionSyncOutcome.Failed(
                $"None of the {skipped} item(s) in the response could be mapped: {firstSkip}.");
        }

        // Every row lands in exactly one of the other four, so the rest were excluded.
        var excluded = payload.Rows.Count - created - updated - unchanged - skipped;

        return new CollectionSyncOutcome(true, created, updated, unchanged, skipped, null, excluded)
        {
            Produced = produced,
            ReadAll = payload.Rows.Count > 0 && !payload.Truncated && !fetched.HasNextPage && skipped == 0,
        };
    }

    private async Task<(string? Body, string? Error, bool HasNextPage)> FetchAsync(CollectionSync sync, CancellationToken ct)
    {
        if (sync.Source == SyncSource.Feed)
        {
            if (!Uri.TryCreate(sync.FeedUrl, UriKind.Absolute, out var feed)
                || (feed.Scheme != Uri.UriSchemeHttp && feed.Scheme != Uri.UriSchemeHttps))
            {
                return (null, "The feed URL is not an absolute http or https URL.", false);
            }

            var feedResult = await fetcher.FetchAsync(
                connector: null,
                new ComposedRequest("GET", feed.AbsoluteUri, new Dictionary<string, string>(), null, null),
                MaxResponseBytes, ct);

            return feedResult.Succeeded
                ? (feedResult.Body, null, feedResult.HasNextPage)
                : (null, Describe(feedResult), false);
        }

        var definition = await session.Query<RequestDefinition>()
            .FirstOrDefaultAsync(r => r.Slug == sync.RequestSlug, ct);

        if (definition is null)
        {
            return (null, $"No request definition with the slug '{sync.RequestSlug}'.", false);
        }

        var connector = await session.Query<Connector>()
            .FirstOrDefaultAsync(c => c.Slug == definition.ConnectorSlug, ct);

        if (connector is null)
        {
            return (null,
                $"Request '{definition.Slug}' names connector '{definition.ConnectorSlug}', which does not exist.", false);
        }

        if (!connector.Enabled)
        {
            return (null, $"Connector '{connector.Slug}' is disabled.", false);
        }

        // Composed against an empty entry of the target type. A sync request's templates address the
        // source rather than an entry, so there is nothing for a {{field}} hole to resolve against,
        // and passing the type means the composer's refusal of a Sensitive field still runs.
        var composed = await composer.ComposeAsync(
            definition,
            connector,
            new Content { ContentType = sync.ContentType },
            idempotencyKey: null,
            ct);

        if (!composed.Ok)
        {
            return (null, composed.Refusal, false);
        }

        var result = await fetcher.FetchAsync(connector, composed, MaxResponseBytes, ct);

        return result.Succeeded ? (result.Body, null, result.HasNextPage) : (null, Describe(result), false);
    }

    private static string Describe(ConnectorFetchResult result) =>
        result.Error ?? (result.StatusCode is { } code ? $"The provider answered {code}." : "The fetch failed.");

    /// <summary>
    /// The mapped fields of one item, or null with the reason it cannot be an entry.
    /// </summary>
    /// <remarks>
    /// A missing source value is not a reason. A feed entry with no category, or a package with no
    /// description, is ordinary, and dropping the whole item over it would mean one incomplete row
    /// emptying a page. Only the key is required, and a value that cannot be converted to the
    /// field's declared type fails the item, because storing "n/a" in an int field produces an entry
    /// the validator would have refused from anybody else.
    /// </remarks>
    private static Dictionary<string, object>? Map(
        CollectionSync sync,
        ContentTypeDefinition schema,
        IReadOnlyDictionary<string, string> row,
        out string? key,
        out string? reason)
    {
        reason = null;
        key = null;
        var mapped = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var (field, path) in sync.FieldMap)
        {
            var definition = schema.Fields.FirstOrDefault(
                f => string.Equals(f.Name, field, StringComparison.OrdinalIgnoreCase));

            // The save-time validator refuses a mapping onto a field the type does not have, so this
            // is a field removed from the type after the sync was configured. Skipped rather than
            // written, since writing it would put a key in the bag that no schema describes.
            if (definition is null) continue;

            if (!row.TryGetValue(path, out var text) || text.Length == 0) continue;

            if (!TryConvert(text, definition.Type, out var value))
            {
                reason = $"'{path}' does not convert to the {definition.Type} field '{field}'";
                return null;
            }

            mapped[definition.Name] = value!;

            if (string.Equals(field, sync.KeyField, StringComparison.OrdinalIgnoreCase))
            {
                key = text;
            }
        }

        foreach (var (field, rule) in sync.FieldRules)
        {
            var definition = schema.Fields.FirstOrDefault(
                f => string.Equals(f.Name, field, StringComparison.OrdinalIgnoreCase));

            if (definition is null) continue;

            switch (SyncRules.Evaluate(rule, row))
            {
                case List<string> list when string.Equals(definition.Type, "array", StringComparison.OrdinalIgnoreCase):
                    mapped[definition.Name] = list;
                    break;

                case string text:
                    if (!TryConvert(text, definition.Type, out var value))
                    {
                        reason = $"the rule for '{field}' made a value that does not convert to the {definition.Type} field";
                        return null;
                    }

                    mapped[definition.Name] = value!;

                    if (string.Equals(field, sync.KeyField, StringComparison.OrdinalIgnoreCase))
                    {
                        key = text;
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            reason ??= $"an item has no value for the key field '{sync.KeyField}'";
            return null;
        }

        return mapped;
    }

    private static bool TryConvert(string text, string fieldType, out object? value)
    {
        switch (fieldType.ToLowerInvariant())
        {
            case "int":
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                {
                    value = whole;
                    return true;
                }
                break;

            case "decimal" or "money":
                if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    value = number;
                    return true;
                }
                break;

            case "bool":
                if (bool.TryParse(text, out var flag))
                {
                    value = flag;
                    return true;
                }
                break;

            case "date" or "datetime":
                if (DateTimeOffset.TryParse(
                        text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment))
                {
                    value = moment.UtcDateTime;
                    return true;
                }
                break;

            default:
                value = text;
                return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// The greater of the stored and the fetched value, for a field the sync declares a floor on.
    /// </summary>
    /// <remarks>
    /// A download count is what this exists for. A registry index that is lagging answers a smaller
    /// number than it did an hour ago, and writing that walks the count backwards on a page that is
    /// only ever meant to go up. The floor is opt in per field, because for a price or a stock level
    /// the smaller number is the true one.
    /// </remarks>
    private static object Floor(
        CollectionSync sync, string field, IReadOnlyDictionary<string, object> stored, object fetched)
    {
        if (!sync.FloorFields.Contains(field, StringComparer.OrdinalIgnoreCase)) return fetched;
        if (!stored.TryGetValue(field, out var current)) return fetched;

        return AsDecimal(current) is { } was && AsDecimal(fetched) is { } now && was > now ? current : fetched;
    }

    /// <summary>
    /// A stored or fetched value as a number, or null when it is not one.
    /// </summary>
    /// <remarks>
    /// A value read back out of the JSON bag arrives as whatever the serializer made of it, which
    /// for a number is a <c>JsonElement</c> or a boxed <c>long</c> depending on the path it took.
    /// Comparing those with <c>IComparable</c> across types throws, so both sides go through decimal.
    /// </remarks>
    private static decimal? AsDecimal(object? value) => value switch
    {
        null => null,
        decimal d => d,
        long l => l,
        int i => i,
        double db => (decimal)db,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } e
            => e.TryGetDecimal(out var parsed) ? parsed : null,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var text) => text,
        _ => null,
    };

    /// <summary>Does the merged data differ from what is stored?</summary>
    /// <remarks>
    /// Asked before every write, and it is not an optimisation. A sync that appended a
    /// ContentUpdated for every entry on every tick would add a hundred events an hour to a hundred
    /// streams for data that did not change, fire every "Updated" workflow each time, and move every
    /// entry to the top of any recently-updated list once an hour, permanently.
    ///
    /// Compared by the text the serializer would write, because the two sides are a freshly
    /// converted value and one that has been through JSON, so <c>1200L</c> and a JsonElement holding
    /// 1200 are the same stored value spelled two ways.
    /// </remarks>
    private static bool Changed(IReadOnlyDictionary<string, object> stored, IReadOnlyDictionary<string, object> merged)
    {
        if (stored.Count != merged.Count) return true;

        foreach (var (field, value) in merged)
        {
            if (!stored.TryGetValue(field, out var current)) return true;
            if (!Same(current, value)) return true;
        }

        return false;
    }

    /// <summary>Is a stored value the same as the one about to be written?</summary>
    /// <remarks>
    /// A fetched datetime is compared as an instant. The stored one is the text the serializer
    /// wrote, "2026-09-21T10:12:29Z", and formatting the fetched one gives seven fractional digits,
    /// so compared as text they never matched and every run rewrote every entry (#987).
    /// </remarks>
    private static bool Same(object? stored, object? fresh) => fresh switch
    {
        DateTime moment => Instant(stored) == moment.ToUniversalTime(),
        System.Collections.IEnumerable items and not string and not System.Collections.IDictionary
            => Elements(stored) is { } was && was.Select(Text).SequenceEqual(items.Cast<object?>().Select(Text)),
        _ => string.Equals(Text(stored), Text(fresh), StringComparison.Ordinal),
    };

    private static DateTime? Instant(object? value) => value switch
    {
        DateTime moment => moment.ToUniversalTime(),
        DateTimeOffset moment => moment.UtcDateTime,
        string or System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String }
            when DateTimeOffset.TryParse(Text(value), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed) => parsed.UtcDateTime,
        _ => null,
    };

    private static IEnumerable<object?>? Elements(object? value) => value switch
    {
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } array
            => array.EnumerateArray().Select(e => (object?)e),
        System.Collections.IEnumerable items and not string and not System.Collections.IDictionary
            => items.Cast<object?>(),
        _ => null,
    };

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        System.Text.Json.JsonElement element => element.ValueKind == System.Text.Json.JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText(),
        DateTime moment => moment.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        System.Collections.IEnumerable items and not string and not System.Collections.IDictionary
            => string.Join(' ', items.Cast<object?>().Select(Text)),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Every path the sync's mapping, rules and exclusions read.</summary>
    private static List<string> MappedPaths(CollectionSync sync) =>
        sync.FieldMap.Values
            .Concat(sync.FieldRules.Values.SelectMany(SyncRules.PathsOf))
            .Concat(sync.Exclude.Select(e => e.Path))
            .ToList();

    /// <summary>The same derived search text the create and update endpoints write.</summary>
    private static string SearchText(IReadOnlyDictionary<string, object> data, ContentTypeDefinition schema)
    {
        var publicFields = schema.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return string.Join(
            ' ',
            data.Where(kv => publicFields.Contains(kv.Key))
                .Select(kv => Text(kv.Value))
                .Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    /// <summary>
    /// The id an entry with this key takes, in this collection.
    /// </summary>
    /// <remarks>
    /// Derived rather than looked up, which is what makes a re-sync an update instead of a
    /// duplicate: the same key always addresses the same entry, in one load, whatever order the
    /// source answers in and however many entries the collection already holds. Querying the JSON
    /// bag for the key instead would be a scan per item per tick.
    ///
    /// Keyed on the content type rather than on the sync's slug, so renaming a sync does not orphan
    /// everything it has written. The consequence is the right one: two syncs filling the same
    /// collection with the same key are writing the same entry, and should be.
    /// </remarks>
    internal static Guid EntryId(string contentType, string key)
    {
        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes($"barakocms:collection-sync:{contentType.ToLowerInvariant()}:{key}"));

        var bytes = digest.AsSpan(0, 16).ToArray();

        // Version 8 (custom) and the RFC 4122 variant, so what is stored is a well formed UUID
        // rather than sixteen arbitrary bytes that happen to fit the column.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }
}
