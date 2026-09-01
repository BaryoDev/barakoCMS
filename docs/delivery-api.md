# Public delivery API

The anonymous read surface a website frontend talks to. No token, no cookie. It is separate from
`/api/contents`, which is the authoring API and requires a bearer token.

Three things have to be true before an entry is delivered, and none of them can be turned off by a
query parameter:

- its content type has `isPubliclyDeliverable: true`,
- the entry's status is `Published`, and
- the entry's document sensitivity is `Public`.

Any field the content type marks as something other than `Public` is stripped from the payload.

A type that has not opted in and a type that does not exist both return 404. That is deliberate:
answering differently would confirm which types exist.

Responses carry `Cache-Control: public, max-age=60`. The one exception is a slug read served under a
valid `?preview=` token, which is `no-store` because it can return an unpublished entry.

**Preview tokens are minted through the API, not the admin.** `POST /api/preview` returns a token
bound to a tenant, a content type and a slug. There is no button for it in barakoCMS itself, so a
front end that wants preview links calls that endpoint from its own code. Deferred deliberately
rather than overlooked (#306), and recorded here so nobody goes looking for a screen that does not
exist.

## Routes

| Method | Route | Returns |
| --- | --- | --- |
| GET | `/api/public/{type}` | a page of published entries |
| GET | `/api/public/{type}/{slug}` | one entry by slug |
| GET | `/api/public/{type}/search?q=` | ranked matches, not a page |
| GET | `/api/public/{type}/feed.xml` | RSS |
| GET | `/api/public/sitemap.xml` | sitemap |

`/{type}/{slug}` needs the type to have a slug field: a field of type `slug`, or failing that a
field named `slug`. Without one the route is 404.

## Pagination

`GET /api/public/{type}` takes `page` and `pageSize`.

| Parameter | Default | Range |
| --- | --- | --- |
| `page` | 1 | 1-indexed. Anything below 1 is clamped to 1 |
| `pageSize` | 20 | clamped to 1..100 |

The cap is 100 whatever the caller asks for, so a page size of 5000 returns 100 rather than an
error. Every paginated endpoint in barakoCMS returns the same envelope:

```json
{
  "items": [],
  "page": 1,
  "pageSize": 20,
  "totalItems": 137,
  "totalPages": 7,
  "hasNextPage": true,
  "hasPreviousPage": false
}
```

`totalItems` counts everything matching the query, filters included, not just the current page.

An entry looks like this:

```json
{
  "id": "0198b0e9-...",
  "contentType": "blog-post",
  "slug": "hello-world",
  "data": { "title": "Hello world", "publishedAt": "2026-08-30" },
  "createdAt": "2026-08-30T02:11:04.113507Z",
  "updatedAt": "2026-08-30T02:11:04.113507Z"
}
```

`data` holds only the fields the type marks `Public`.

## Filtering

```text
filter[field][op]=value
```

Repeat the parameter for more than one filter. Each repeat is its own filter, including repeats of
the same field, so `?filter[tag][eq]=a&filter[tag][eq]=b` is two filters rather than one filter for
the literal `a,b`.

Operators:

| Op | Meaning |
| --- | --- |
| `eq` | equals |
| `ne` | not equal |
| `lt` `lte` `gt` `gte` | ordered comparison |
| `contains` | case-insensitive substring |

At most **five filters** per request. A sixth returns 400. The cap is there because arbitrary filter
combinations against a JSONB column on an anonymous endpoint is a denial-of-service surface.

Only fields the type marks `Public` can be filtered. Naming any other field returns 400 rather than
being ignored, because filtering on a field you cannot read is an oracle: the value never appears in
a response, but which entries come back reveals it. A silently ignored filter is worse still, since
the caller cannot tell "no filter applied" from "no matches".

Comparison happens in jsonb using the field's declared type, so a numeric field compares
numerically: `filter[price][lt]=10` puts 9 below 10 instead of after it.

Filters narrow what the published-and-public predicate already allows. No filter can widen it.

```text
GET /api/public/blog-post?filter[category][eq]=engineering&filter[title][contains]=marten
```

## Sorting

```text
sort=field      ascending
sort=-field     descending
```

Same allowlist as filtering: a field the type does not mark `Public` returns 400. A requested sort
replaces the default (`createdAt` descending) rather than adding to it, and `createdAt` stays as the
tiebreaker so a page boundary cannot move between two entries that compare equal.

Entries missing the sort field collect at the end in both directions.

`sortBy` was removed in 4.0. It was accepted here and honoured nowhere.

## Resolving references

```text
include=author,category
```

Replaces reference ids with the referenced entries, in one batched load rather than one request per
reference. At most **five** includes per request.

A named field must be of type `reference` and marked `Public`, or the request is 400. Each resolved
entry goes through the same projection as the list itself, so published state, document sensitivity,
type opt-in and the field allowlist all still apply. A target that does not survive that projection
has its field removed rather than left as an id, which makes an unreadable target
indistinguishable from no reference at all.

## Search

```text
GET /api/public/{type}/search?q=marten&limit=20
```

`limit` is clamped to 1..50, default 20. A `q` shorter than two characters returns an empty result
rather than an error.

This is the one delivery endpoint that does **not** take the pagination envelope:

```json
{ "results": [], "count": 0, "query": "marten" }
```

There is no stable ordering to page through and no total beyond the scan cap, so a caller asking for
page 3 of a relevance ranking would get something that changes underneath them. `count` is how many
of a bounded, ranked scan matched, not how many exist. Matching runs over public fields only, and a
title or name hit outranks a body hit.

## Errors

| Status | When |
| --- | --- |
| 400 | unknown filter field, unknown operator, malformed `filter[...]`, more than 5 filters, unknown or non-reference `include`, more than 5 includes, unsortable field |
| 404 | unknown type, type not marked publicly deliverable, no slug field, no published entry at that slug |

A 400 carries the reason, including the fields that would have been accepted.
