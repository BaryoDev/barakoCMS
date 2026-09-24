# Pushing entries to a collection

A [collection sync](collection-syncs.md) is the CMS deciding when to look. Some sources know the
moment they changed, and no API answers for them: a repository's `CHANGELOG.md`, its contributor
roster, its docs folder. For those the source pushes, from its own CI, when it changes.

```
POST /api/collections/{type}/push
```

`{type}` is an existing content type in the caller's tenant. A push only fills a type; it never
creates one.

## The key

Mint a key limited to the types the push fills. An admin with `manage_api_keys` does this:

```bash
curl -s https://cms.example.com/api/api-keys \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{ "name": "changelog CI", "scopes": ["content:write"], "contentTypes": ["release"] }'
```

The response holds the secret once. A key that names `contentTypes` reaches
`POST /api/collections/{type}/push` for those types and gets 403 on every other route, whatever its
scopes. It needs `content:write`. It acts in its own tenant only, and as the admin who minted it, so
it can never do more than that admin could. Revoke it with `DELETE /api/api-keys/{id}`.

A signed-in user, or a key that names no types, can push too, with the same permission checks.

## The request

```json
{
  "entries": [
    { "slug": "4-3-0", "title": "4.3.0", "date": "2026-09-20", "body": "..." },
    { "slug": "4-2-0", "title": "4.2.0", "date": "2026-09-02", "body": "..." }
  ],
  "archiveMissing": false,
  "status": "Published"
}
```

Each entry is the field values of one entry. The type's slug field (the field of type `slug`, or
the field named `slug`) is the key: an entry whose slug is already stored updates that entry, and
one whose slug is not creates it. A type with no slug field cannot be pushed to.

`status` is `Published` or `Draft`, and defaults to `Published`. An entry in the push ends at that
status, so an entry archived earlier is published again when the push carries it again.

## What happens to each entry

Every entry goes through what `POST /api/contents` and `PUT /api/contents/{id}` run: the type's
`create` or `update` permission, the write-path sensitivity rule, the schema validator (types,
required fields, slug uniqueness, references) and any module lifecycle hooks. A push is a faster
way in, not a looser one.

An entry whose data and status are the same as what is stored is left alone: no new version, no
event, no workflow and no webhook. A CI job can push the whole collection on every commit and only
what changed is written.

The whole push is one transaction. If any entry is refused, nothing is written and the response is
400 with every refused entry listed. If another writer changes an entry while the push runs, the push
is refused with 409 and nothing is written; push again.

## archiveMissing

`"archiveMissing": true` archives every published entry of the type whose slug is not in the push.
It runs only once every entry in the push has passed, in the same transaction, so a push with a
refused entry archives nothing. Drafts and entries with no slug are left alone. A push that would
archive more than 1,000 entries is refused. Each archive is audited as `content.archived`, the same
as archiving by hand, and a type with a lifecycle cannot be pushed with `archiveMissing`, because its
entries move by named transitions.

Only turn it on when the push carries the whole collection, the way a changelog push carries every
release.

## The response

```json
{ "created": 1, "updated": 1, "unchanged": 38, "archived": 0, "errors": [] }
```

On a refused push, 400 and the entries that were refused:

```json
{
  "created": 0, "updated": 0, "unchanged": 0, "archived": 0,
  "errors": [
    { "index": 2, "slug": "broken", "messages": ["Field 'Count' expects type 'int' but received 'string'"] }
  ]
}
```

A malformed request (no entries, too many, a status that is not `Draft` or `Published`) is a 400 in
the usual validation shape instead.

| Status | Meaning |
| --- | --- |
| 200 | Written. The counts say what changed. |
| 400 | Refused entries, a malformed request, or a type with no slug field. Nothing written. |
| 401 | No credentials. |
| 403 | The key is limited to other types or lacks `content:write`, or the caller may not create or update entries of this type. |
| 404 | No such content type in the caller's tenant. |
| 409 | A concurrent write, or a repeated `Idempotency-Key`. Nothing written. |
| 413 | The body is over the size limit. |

## Limits

At most 1,000 entries and 4 MB per push, or the server's `RequestLimits:MaxBodyBytes` if that is
smaller.

## Retries and webhooks

`Idempotency-Key` works here as on every other write (see [idempotency.md](idempotency.md)): a
repeat of a key that succeeded is answered 409, and a key whose push failed can be retried. Pushing
the same entries again without a key is also safe, since unchanged entries are not written.

Workflows and their webhooks fire from the events a push appends, the same as for the content API:
once per created entry (`Created`), once per changed entry (`Updated`), and `Published` when a
push moves an entry to published. An unchanged entry fires nothing. There is no single "push"
event; a renderer that rebuilds per change gets one delivery per changed entry.

## From CI

A GitHub Actions job that pushes the changelog when it changes. The script that turns
`CHANGELOG.md` into entries is the repository's own; the push is one request.

```yaml
name: push changelog
on:
  push:
    branches: [master]
    paths: [CHANGELOG.md]

jobs:
  push:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: node scripts/changelog-to-entries.mjs CHANGELOG.md > entries.json
      - run: |
          jq '{ entries: ., archiveMissing: true }' entries.json > push.json
          curl --fail-with-body -s "$CMS_URL/api/collections/release/push" \
            -H "Authorization: Bearer $CMS_PUSH_KEY" \
            -H "Content-Type: application/json" \
            -H "Idempotency-Key: $GITHUB_SHA" \
            --data @push.json
        env:
          CMS_URL: ${{ vars.CMS_URL }}
          CMS_PUSH_KEY: ${{ secrets.CMS_PUSH_KEY }}
```

`--fail-with-body` fails the job on a refused push and prints which entries were refused.
