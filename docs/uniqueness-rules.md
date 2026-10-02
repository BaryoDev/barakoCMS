# Uniqueness rules

A content type can say which values only one of its entries may hold at a time. A teacher time
clock is the example this was built for: Time in creates a `timeEntry` in `Open`, Time out is a
`ClockOut` transition to `Closed`, and a teacher may hold one open entry. A workflow cannot enforce
that, because it runs after the save commits. A uniqueness rule is checked inside the write.

`UniquenessRuleTests`, `UniquenessExistingEntriesTests`, `UniquenessWritePathTests`,
`UniquenessImportTests` and `BlueprintUniquenessTests` are the proof for what this page says.

## Declaring a rule

```bash
curl -s -X POST $BASE/api/content-types -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d '{
  "name": "timeentry",
  "displayName": "Time entry",
  "fields": [ { "name": "Note", "displayName": "Note", "type": "string" } ],
  "lifecycle": {
    "states": ["Open", "Closed"],
    "initialState": "Open",
    "transitions": [ { "name": "ClockOut", "from": "Open", "to": "Closed" } ]
  },
  "uniqueness": [
    { "name": "OneOpenEntryPerTeacher", "fields": ["$createdBy"], "whenState": "Open" }
  ]
}'
```

- `name`: PascalCase, letters and digits, at most 64 characters, once per type ignoring case.
  The refusal names it.
- `fields`: one to five fields of the type, compared together, or `$createdBy` for the user who
  created the entry. A field has to be Public and hold one value: `string`, `text`, `int`,
  `decimal`, `money`, `bool`, `email`, `url`, `slug`, `uuid`, `reference`, or a `choice` that
  takes one option. `GET /api/meta/describe` lists these under `uniqueness`. Any other field type
  is refused, including one added later until it is placed on that list.
- `date`, `datetime` and `time` are refused. The API accepts one instant or one time of day in many
  spellings (`2026-10-02T00:00:00Z` and `2026-10-02T08:00:00+08:00`, `9:00` and `09:00`) and
  stores each as written, so a rule comparing them as written would let the same value in twice,
  and the database cannot read every spelling .NET accepts to compare them as values.
- `whenState`: optional. A lifecycle state of the type. The rule then counts only entries in that
  state, so an entry that leaves it frees its values. Without it every entry of the type counts,
  whatever its state or status.

A type takes at most five rules. Anything else is a `400` naming the rule and what is wrong.

A Sensitive or Hidden field cannot be in a rule, and a field a rule names cannot be raised from
Public (`PUT /api/content-types/{name}/fields/{field}/sensitivity` answers `400`). A refusal tells
the caller that some entry holds the value they sent, and on a field they may not read that is a
way to test what it holds.

## How values are compared

PostgreSQL compares them, as `jsonb`, the same way for the check and for the lock:

- Text exactly. `B-1`, `b-1` and `B-1 ` are three values. No culture, no trimming.
- Numbers by value. `1.5` and `1.50` are one value.
- Text is never equal to a number: `"5"` and `5` are two values.
- An id (`uuid` or `reference`) ignoring case, braces, parentheses and dashes, since the API
  accepts an id in any of those spellings and stores it as written. An id written in the `0x`
  hexadecimal form is compared as written.
- An `email` with the capitals A to Z lowered, the whole address: `Teacher@School.Example` and
  `teacher@school.example` are one address. Letters outside A to Z are compared as written,
  because the database and .NET need not lower them alike.
- A `url` and anything else of type text exactly, as written.
- `$createdBy` by user id.
- An entry with no value in one of the fields of a rule (the field missing, `null` or `""`) is
  outside that rule, as a row holding NULL is outside a unique index. So is an entry nobody created
  through a signed-in request (a public form submission, a sync, an import run by the system), for
  a rule on `$createdBy`.
- A field is found in an entry by name ignoring case. Where an entry holds a name in two
  spellings, the one that sorts first by code point is read, by both sides.
- Several fields in one rule are compared together, in the order the rule lists them.

## What is refused

A write that would leave the entry counted by a rule while another entry counted by the same rule
holds the same values. The answer is `409`, a problem details body like any other refusal, whose
one error reads:

```
The rule 'OneOpenEntryPerTeacher' on 'timeentry' allows one entry per value while Open, and another entry already holds this one.
```

It names the rule and the type. It does not name the entry holding the values or repeat the values,
because the caller may have no right to read that entry. Nothing of the refused write is stored.

| Write | How the rule holds |
| --- | --- |
| `POST /api/contents` | Checked when the writer stages the create. `409`. |
| `PUT /api/contents/{id}` | Checked on the entry as the write leaves it. `409`. |
| `PUT /api/contents/{id}/status` with `newStatus`, scheduled publish and unpublish | Status is not compared, so the values do not change; checked like any write and not refused for values the entry already held. |
| `PUT /api/contents/{id}/status` with `transition`, and `IContentTransitioner` from code | A move into `whenState` is checked. The endpoint answers `409`; from code the outcome is `Conflict` with the message. |
| `POST /api/contents/{id}/rollback/{versionId}` | Checked on the restored values. `409`. |
| `POST /api/import/content` | A row holding values a stored entry or an earlier row holds is a row error, so `continueOnError` still writes the others. |
| `POST /api/portability/import` | A record holding values a stored entry or an earlier record holds is refused by its index, `400`, and nothing is imported. |
| Collection push | Checked on each entry. `409`, and nothing of the push is written. |
| Collection sync | Checked on each entry. The run stops at that item and is logged at error level; the items written before it stay. |
| `POST /api/public/forms/{slug}` | Checked like a create, `409`. A submission has no creator, so a `$createdBy` rule does not count it. |
| Workflow `UpdateField` and `CreateTask` | Checked; the action fails and the run records the failure. `UpdateField` checks an entry with no event stream too. |
| `POST /api/accounting/accounts` and `AccountService.UpsertAsync` | They store account entries with `session.Store`, around the writer, and apply the account type's rules themselves through `ContentWriter.CheckUniquenessAsync`. `409` from the route. |
| `DELETE /api/contents/{id}/erase` and any delete | Nothing to release: the check reads the stored entries, and a removed entry holds nothing. |
| Event-sourced types | The writer folds the stream and checks the entry it is about to store, as for every other type. |

Two writes go around the content writer and are not checked: the stream rebuild
(`POST /api/content-types/{name}/rebuild`), which stores what the stream already says, and the data
seeder. Neither takes values from a caller. A module that stores entries with `session.Store`
applies the rules by calling `ContentWriter.CheckUniquenessAsync` before it stores, as the
accounting module does.

A write that is refused because another write of the same values has not finished within five
seconds (see below) is also a `409`, whose message says to try again.

### What a refusal tells the caller

A `409` says that some entry holds the values sent. That is the feature, and it is also an answer
to "does an entry hold this value" for anyone who can make the write:

- An anonymous visitor submitting a public form on a type with a rule (one submission per email,
  say) learns whether an address has been used. Form submissions are rate limited (the `forms`
  policy: by default five submissions per client address every ten minutes across all forms), which
  slows this down and does not stop it.
- A caller whose read is limited by a row rule learns that an entry they cannot see holds the
  value, though not which entry.

Sensitive and Hidden fields cannot be in a rule for this reason. For a Public field on a type with
a public form, decide whether that answer is acceptable before declaring the rule.

## What makes it hold

Two requests at once each reading "no other entry holds this" and both writing is the failure this
exists to stop, so the check alone is not enough. Each write takes a PostgreSQL advisory lock in its
own transaction, keyed on the tenant, the type, the rule and a hash of the values, before it reads.
A second write of the same values keeps trying for the lock until the first commits or rolls back,
then reads what the first left. Writes of other values do not wait. The lock is released by the
commit or the rollback, so a crash leaves nothing behind and a rerun starts clean.

The lock is tried, not waited on, for up to five seconds. Inside a spreadsheet or bundle import the
holder's transaction lasts until the whole import commits, so a write of a value an import row took
gives up after five seconds with a `409` that says to try again, rather than waiting for the import.
For the same reason two imports taking the same values in opposite orders cannot deadlock: each
gives up on the row the other holds, which becomes that row's error. There is no record of who holds what to
release when an entry leaves the state, changes its values or is erased: the entries are what is
read.

There is no unique index behind it. Every content type of every tenant is one table, and an index
per rule would be a schema change made at run time by configuration, which this API does not do.
So there is no migration for this.

## Adding a rule to a type that has entries

```bash
curl -s -X PUT $BASE/api/content-types/timeentry/uniqueness -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d '{
  "uniqueness": [ { "name": "OneOpenEntryPerTeacher", "fields": ["$createdBy"], "whenState": "Open" } ]
}'
```

The list replaces the type's rules; an empty list removes them. Needs `manage_content_types` and is
recorded in the audit log as `contenttype.uniqueness.changed`.

For each rule added or changed, the entries that already share their values with another entry the
rule counts are counted. If there are any, the answer is `409` with the count per rule, and nothing
is stored. Send `"force": true` to store the rule anyway; the `200` carries the counts in
`duplicates`. Those entries are not changed:

- They are read, listed and delivered as before.
- An edit that leaves an entry's values as they were is accepted, so they stay editable.
- A write that would bring an entry to values another entry holds is refused, theirs included: one
  of them moved to a free value cannot move back.

List them, oldest first, with:

```bash
curl -s "$BASE/api/content-types/timeentry/uniqueness/OneOpenEntryPerTeacher/duplicates?page=1&pageSize=50" \
  -H "Authorization: Bearer $ADMIN"
```

Ids and creation times only, paged, for a caller with `manage_content_types` and read on the type,
and only the entries the caller's read rules let them see, as the entries list does.

The count is read before the save. A write that read the type before the rule landed can still add
an entry to it.

A rule a save would refuse today and that is stored anyway (a field since removed by an import, a
state the lifecycle no longer has) is skipped on every write and logged at warning level with the
type and the rule's name.

## Blueprints and bundles

A blueprint file and a bundle carry `uniqueness` on a type and are checked the way
`POST /api/content-types` checks it. A bundle importing over a stored type keeps the stored rules
when it carries none, and is refused when it carries different ones or leaves out a field a stored
rule compares; `PUT /api/content-types/{name}/uniqueness` is how a stored type's rules change.

## Limits

- The check reads the type's entries through an expression the content type index does not cover,
  so its cost grows with the tenant's entries of the type. It runs on writes of types that declare
  a rule and only those.
- A write waits at most five seconds for another write of the same values, polling the lock.
- `whenState` names a lifecycle state. Draft, Published and Archived are not states for this.
- The obsolete synchronous `IContentWriter.Create` and `Append` do not apply rules.
