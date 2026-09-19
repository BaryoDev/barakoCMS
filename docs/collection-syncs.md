# Collections filled from outside

Some collections are not typed by anybody. A package list is whatever the registry says, a release
list is whatever GitHub says, a news feed is whatever the publisher published. Three sites were each
carrying their own code to read those at build time, which is the one thing that stopped them being
the same deployment with different data.

A collection sync is that code as configuration. It names a content type, a source, and which value
becomes which field. The entries it writes are ordinary content, so blocks, delivery, search and the
admin treat them like anything else. Nothing marks an entry as synced.

## Managing them

`GET /api/collection-syncs` lists, `GET /api/collection-syncs/{slug}` reads one,
`POST /api/collection-syncs` creates, `PUT /api/collection-syncs/{slug}` edits,
`DELETE /api/collection-syncs/{slug}` removes, and `POST /api/collection-syncs/{slug}/run` runs one
now. All six need the `manage_collection_syncs` capability, which Admin and SuperAdmin hold by
default.

Deleting a sync leaves the entries it wrote. Somebody may be linking to them and a block may be
rendering them, so removing a schedule is not a decision to delete a hundred published pages. That
is `DELETE /api/contents/{id}/erase`, deliberately.

## The source

**A request.** `"source": "Request"` with a `requestSlug`. That is an existing request definition,
sent through its existing connector, so the base URL, the credential, the address guard and the
timeouts are the ones you already configured and tested with the connector's own test button. The
credential never comes near the sync: the sync names the request, the request names the connector,
the connector holds the secret encrypted, and the sender attaches it to the finished message.

**A feed.** `"source": "Feed"` with a `feedUrl`. A plain GET with no credential, for a public RSS or
Atom feed. It goes through the same outbound client, so the same address guard applies. DTDs are
refused: a feed is a document from a third party and a DOCTYPE in one is either an entity expansion
or an external reference the sweep would go and fetch.

## The mapping

```json
POST /api/collection-syncs
{
  "name": "NuGet downloads",
  "slug": "nuget-downloads",
  "contentType": "package",
  "source": "Request",
  "requestSlug": "nuget-search",
  "itemsPath": "data",
  "fieldMap": {
    "packageId": "id",
    "downloads": "totalDownloads",
    "summary":   "description"
  },
  "keyField": "packageId",
  "floorFields": ["downloads"],
  "intervalMinutes": 60,
  "maxEntries": 100,
  "entryStatus": "Published"
}
```

`itemsPath` is the dotted path to the array in the response, empty when the response is itself an
array. The NuGet search API answers `{ "totalHits": 9, "data": [ ... ] }`, so it is `data`.

`fieldMap` is keyed by the content field and valued by the path within one item: `id`,
`totalDownloads`, `authors[0]`, `versions[0].version`. For a feed the paths are a single vocabulary
covering both dialects, so a mapping written against RSS keeps working if the publisher moves to
Atom: `id`, `title`, `link`, `summary`, `content`, `published`, `author`, `category`. Dates arrive as
UTC ISO 8601 whichever dialect they were written in.

Everything is checked when you save, not on the first sweep. A field the content type does not have,
a field that is not `Public`, a floor on a field that is not numeric, a feed path outside the
vocabulary above (`pubDate` is a common one; both dialects read into `published`), a request
definition or connector that does not exist: each is a 400 while you still have the form open. The
failure this guards against is a sync that is configured, looks fine in the list, and quietly writes
nothing at three in the morning.

## The key

`keyField` names the field holding the stable key. The entry's id is derived from the content type
and that value, so the same key always addresses the same entry: a re-sync updates rather than
duplicating, whatever order the source answers in and however many entries the collection holds.

It is keyed on the content type rather than on the sync, so renaming a sync does not orphan
everything it has written, and two syncs filling one collection with the same key are writing the
same entry.

## The floor

`floorFields` names fields kept at the greater of the stored and the fetched value. A registry index
that is lagging answers a smaller download count than it did an hour ago, and writing that walks the
number backwards on a page that is only ever meant to go up.

It is opt in per field, because for a price or a stock level the smaller number is the true one. The
floor applies to the named field and nothing else, so an edited description still moves down.

## The schedule

`intervalMinutes` is how long after a run the next one is due, at least 5. A background sweep looks
for due syncs once a minute, takes a Postgres advisory lock so only one instance sweeps, and runs at
most twenty syncs per tick. `maxEntries` caps how many items of one response are written, up to 500:
nothing here follows a source's paging, and a source answering ten thousand items must not turn one
tick into ten thousand writes.

A response that says the same thing writes nothing at all. Without that, every tick would append a
`ContentUpdated` to every entry's stream, fire every Updated workflow, and move the whole collection
to the top of any recently-updated list once an hour, forever.

`CollectionSyncs:Enabled=false` turns the schedule off. Set it on a staging copy of a production
database, which would otherwise call every one of production's providers on production's interval.
A sync can still be run from the API with the schedule off, which is the difference between turning
the schedule off and disabling the sync.

## When it fails

A failed fetch touches no entries. The collection keeps serving what the last good run wrote, and
the sync records what happened:

```json
GET /api/collection-syncs/nuget-downloads
{
  "lastRunAt": "2026-09-19T03:00:11Z",
  "lastSuccessAt": "2026-09-19T02:00:09Z",
  "lastEntryCount": 9,
  "lastError": "The provider answered 403.",
  "consecutiveFailures": 1
}
```

`lastSuccessAt` and `lastEntryCount` stay where the last good run put them. That pair is what tells
an operator the page is real but stale rather than empty.

`lastError` is never a response body. A 401 from an OAuth provider frequently echoes the credential
that was sent, and this field is read back by an admin screen and stored for as long as the sync
exists. It carries a status code and a sentence, the same as a connector test does.
