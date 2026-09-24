# Collections filled from outside

Some collections are not typed by anybody. A package list is whatever the registry says, a release
list is whatever GitHub says, a news feed is whatever the publisher published. Three sites were each
carrying their own code to read those at build time, which is the one thing that stopped them being
the same deployment with different data.

A collection sync is that code as configuration. It names a content type, a source, and which value
becomes which field. The entries it writes are ordinary content, so blocks, delivery, search and the
admin treat them like anything else. Nothing marks an entry as synced.

A sync is the CMS deciding when to look. When the source knows the moment it changed, a repository's
changelog for instance, let it push instead: see [collection-push.md](collection-push.md).

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

## Field rules

`fieldMap` copies a value the item already holds. `fieldRules` builds one, keyed by the content field
the same way. A field is named in one of the two, never both, and a definition without `fieldRules`
runs exactly as it always did.

Each rule has exactly one of `const`, `path` or `ratio`.

**A constant.** The same value for every item, for a source that cannot say what the field needs. A
GitHub milestone does not name its repository, so each of four milestone syncs says which product
it is for:

```json
"fieldRules": { "Product": { "const": "cms" } }
```

**A path with a transform.** `path` is a dotted path as in `fieldMap`. On top of it:

- `prefixStrip` removes a prefix when the value starts with it, and leaves any other value alone.
- `regex` takes a pattern with exactly one capture group, and the group is the value. A value the
  pattern does not match writes nothing. Patterns run in linear time, so lookarounds and
  backreferences are refused when you save.
- `join` joins every element of an array path with a separator.

When more than one applies, the order is prefix strip, then regex, then join.

```json
"fieldRules": {
  "Path":           { "path": "html_url", "prefixStrip": "https://github.com/" },
  "RepositoryName": { "path": "repository_url", "regex": "repos/[^/]+/([^/]+)$" },
  "Tags":           { "path": "labels[].name", "join": ", " }
}
```

`labels[].name` is an array path: `[]` stands for every element, in order. An array path needs
`join` or `contains`, or a content field of type `array`, which then holds the list itself:

```json
"fieldRules": { "Labels": { "path": "labels[].name" } }
```

**Contains.** True when any value of the path equals the given text, ignoring case, and false
otherwise, including when the array is empty. The field must be `bool`.

```json
"fieldRules": { "FirstIssue": { "path": "labels[].name", "contains": "good first issue" } }
```

**A ratio.** Two paths, closed then open. Writes closed over closed plus open as a percent rounded
to a whole number, and 0 when both are 0. The field must be `int` or `decimal`.

```json
"fieldRules": { "Percent": { "ratio": ["closed_issues", "open_issues"] } }
```

A milestone with 3 closed and 1 open writes 75; 2 and 1 writes 67.

The key field may be a path rule, so a key can come out of a regex. It cannot be a `const`, `ratio`
or `contains` rule, since every item would share the key.

## Excluding items

`exclude` is a list of rules, and an item matching any one of them is skipped before it is mapped.
Each rule has a `path` and exactly one of:

- `"notEmpty": true` skips the item when the path holds anything: a value, or a non-empty array or
  object.
- `"equalTo": "..."` skips it when any value of the path equals the text, ignoring case.

```json
"exclude": [
  { "path": "assignees", "notEmpty": true },
  { "path": "id", "equalTo": "BarakoCMS" }
]
```

An excluded item is counted as `excluded` in a run's result, not as `skipped`, and it is not a
failure: a run that excludes every item succeeds with no entries. `maxEntries` counts items before
exclusion.

Exclusion decides what a run writes. It does not remove an entry an earlier run wrote, for the reason
the next section gives: an issue that gets an assignee stays in the collection until somebody
archives it.

Every rule is checked when you save, the same as `fieldMap`: an unknown or non-`Public` field, a
rule with no source or two, a regex that does not compile or does not have one capture group, a
`contains` into a field that is not `bool`, a ratio into one that is not a number, an array path
with nowhere to put a list, and for a feed, a path outside the feed vocabulary. Each is a 400 naming
the field.

## The key

`keyField` names the field holding the stable key. The entry's id is derived from the content type
and that value, so the same key always addresses the same entry: a re-sync updates rather than
duplicating, whatever order the source answers in and however many entries the collection holds.

It is keyed on the content type rather than on the sync, so renaming a sync does not orphan
everything it has written, and two syncs filling one collection with the same key are writing the
same entry.

A sync never removes an entry. A key the source stops answering with is simply not refreshed, so a
package delisted from a registry stays on the page until somebody archives or erases it. That is
deliberate: an entry is ordinary content, a source having a bad afternoon is indistinguishable from
a source that dropped something on purpose, and the recoverable mistake is the one that leaves too
much rather than too little.

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

A response that says the same thing writes nothing at all. A datetime is compared as an instant,
so the same moment written with or without fractional seconds is the same value. Without that, every tick would append a
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
