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

Responses carry `Cache-Control: public, max-age=60` and `Vary: X-Tenant`, because the tenant can be
resolved from the `X-Tenant` header (see `docs/multi-tenancy.md`) and the payload is built entirely
from that tenant's content. The one exception is a slug read served under a valid `?preview=` token,
which is `no-store` because it can return an unpublished entry. `Vary` tells a conforming cache to
key on the header, but it is not a CDN setting on its own: see the caching section of
`docs/deploy-in-production.md` for what the CDN itself has to be configured to do.

**Preview tokens are minted through the API, not the admin.** `POST /api/preview` returns a token
bound to a tenant, a content type and a slug. It is authenticated, and the caller also needs `read`
on the entry being previewed, so minting a token is not a way around the permissions that guard
reading it normally. There is no button for it in barakoCMS itself, so a front end that wants preview
links calls that endpoint from its own code with a token that satisfies both. Deferred deliberately
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
| GET | `/api/public/events` | a server-sent event stream of changes (off by default, see below) |

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
| `near` | within `radiusKm` of `lat,lng`, geopoint fields only; see [The near filter](#the-near-filter) |

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

## Locations and proximity

A field of type `geopoint` holds a coordinate pair:

```json
{ "lat": 6.5031, "lng": 124.8469 }
```

Both keys are required, both must be JSON numbers, latitude within -90..90 and longitude within
-180..180. A string like `"6.5031,124.8469"` is refused on write, because the proximity query casts
the stored numbers and a string would not cast. The admin editor hint is `geopoint`.

### The near filter

```text
filter[field][near]=lat,lng,radiusKm
```

Everything within `radiusKm` of the centre. Only a `geopoint` field marked `Public` can be named,
one `near` per request, and it counts against the five-filter cap. A malformed centre, a centre
outside the valid range, a radius of zero or below, or a radius above the cap is 400 with the
reason. The cap is `Delivery:MaxRadiusKm`, default 1000, and a request above it is refused rather
than quietly narrowed.

The query runs in SQL over the stored JSONB in two stages: a bounding box on the latitude and
longitude, then the haversine distance against the radius. It is applied in the same chain as every
other filter, so the published, public and sensitivity rules apply unchanged: a Draft two kilometres
from the centre is not returned.

Distances are great-circle on a sphere of mean radius 6371 km. That is right for "within 10 km" and
for ordering a list. It is not geodesy: against the ellipsoid it is off by up to a third of a
percent, and a survey or a legal boundary needs PostGIS, which this deliberately does not require.

### Distance in the response

When a `near` filter is present each item carries `distanceKm`, kilometres from the centre to two
decimals, the same number the rows were filtered and ordered by. Without a `near` filter the key is
absent, not null.

`sort=distance` and `sort=-distance` order by it. Without a `near` filter, `sort=distance` is 400,
unless the type has a `Public` field called `Distance`, which then sorts as any other field would.

```bash
curl "https://cms.example.com/api/public/store?filter[Location][near]=6.5031,124.8469,60&sort=distance" \
  -H "X-Tenant: default"
```

```json
{
  "items": [
    { "id": "3f2c1a9e-6b0d-4f7a-9c2e-1d5b8a7e4c01", "data": { "Title": "Koronadal", "Location": { "lat": 6.5031, "lng": 124.8469 } }, "distanceKm": 0 },
    { "id": "8a1e4d2c-0b9f-4e3a-a7c6-2f4d9b8e1c02", "data": { "Title": "General Santos", "Location": { "lat": 6.1164, "lng": 125.1716 } }, "distanceKm": 56.01 }
  ],
  "totalItems": 2
}
```

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
| 400 | unknown filter field, unknown operator, malformed `filter[...]`, more than 5 filters, unknown or non-reference `include`, more than 5 includes, unsortable field, malformed `near` centre or radius, `near` on a field that is not a `geopoint`, `sort=distance` without a `near` filter |
| 404 | unknown type, type not marked publicly deliverable, no slug field, no published entry at that slug |

A 400 carries the reason, including the fields that would have been accepted.

## Change events

`GET /api/public/events` is a [server-sent event](https://html.spec.whatwg.org/multipage/server-sent-events.html)
stream of the tenant's content changes, for a frontend or a dashboard that wants to react to a
publish without polling. Anonymous, like every other route here, and it answers to the same three
rules: an entry is streamed only if its type has opted in, only while it is `Published`, and only
with its `Public` fields. The payload is produced by the same projection the REST reads use, so a
field `GET /api/public/{type}/{slug}` masks is masked here for the same reason: it is the same
function. `ContentEventStreamTests` asserts that directly against a Sensitive field.

It is off by default. `Delivery:Events:Enabled` turns it on; while it is off the route is 404.

**This route must never be cached.** Unlike every other route here it sends `Cache-Control: no-store`,
not `max-age=60`, because a stream has no single response to cache: buffering one connection's frames
and replaying them to a later request is wrong in a single tenant, and a cross-tenant version of the
same mistake is the class of bug #546 exists to close. It sends `Vary: X-Tenant` too, for consistency
with the rest of this surface, though a store honouring `no-store` never needs it. A reverse proxy or
CDN placed in front of this route has to be told two separate things: not to cache it at all (most
already treat `no-store` correctly, but confirm rather than assume), and, independently, not to buffer
the response before forwarding it. A proxy that buffers waits for the stream to end before sending
anything downstream, which breaks Server-Sent Events outright, whether or not caching is involved.
nginx's own setting for this is `proxy_buffering off;` on the route; check the equivalent for whatever
sits in front of it.

```
GET /api/public/events
GET /api/public/events?type=post&type=page
```

`type` repeats to filter by content type. A type that has not opted in never matches, which says
nothing about whether it exists. At most 20 types per connection.

Three event names, and every payload carries `id`, `contentType` and `slug`:

| Event | When | Payload |
| --- | --- | --- |
| `content.published` | an entry became public: published, created as published, or its document sensitivity set back to Public | the entry, in the shape `GET /api/public/{type}/{slug}` returns |
| `content.updated` | a published entry changed: an edit, a lifecycle transition, or a field's sensitivity changed on its type | the entry, same shape |
| `content.unpublished` | a public entry stopped being public: moved to Draft or Archived, or its document sensitivity changed | `id`, `contentType`, `slug` only |

A draft save emits nothing. A draft moved to Archived emits nothing either, because it was never
on anybody's site and an unpublish for it would hand out the slug of an entry the REST API answers
404 for. Erasing an entry (`DELETE /api/contents/{id}/erase`) emits nothing yet.

A comment line (`: keepalive`) is sent whenever nothing else has been for
`Delivery:Events:KeepAliveSeconds` (15), so a proxy does not close an idle connection. An
`EventSource` never dispatches a comment. The frames carry no event id (the `id:` line is empty)
and there is no replay: a client that reconnects should re-read what it cares about, then resume
listening.

```js
const source = new EventSource("/api/public/events?type=post");
source.addEventListener("content.published", e => render(JSON.parse(e.data)));
source.addEventListener("content.updated", e => render(JSON.parse(e.data)));
source.addEventListener("content.unpublished", e => remove(JSON.parse(e.data).id));
```

### Caps

An anonymous long-lived connection is a resource anybody on the internet can hold, which is why
the stream is opt-in, and why it is capped:

- `Delivery:Events:MaxConnections` (100) is the number of open streams across all tenants on one
  instance. The next connection gets 503 with `Retry-After`.
- `Delivery:Events:MaxConnectionsPerClient` (5) is the number of open streams from one client
  address, so one caller cannot hold every slot under the instance cap and starve every other
  tenant on the instance. The next stream from that address gets 503 with `Retry-After` and a body
  that says the client is at its own limit, while another address still connects. The slot comes
  back when the stream closes. The address is the one the rate limiter partitions on: the socket
  peer, or the forwarded client when `ForwardedHeaders` names the proxy. Behind a proxy that is
  not named, every client shares the proxy's address and the cap counts them together. Zero turns
  the per-client cap off.
- Each connection buffers 64 changes. A subscriber that stops reading has its oldest change
  dropped, and the drop is logged once per connection. Nothing a slow subscriber does holds
  memory for anybody else.

### One instance, its own writes

The stream is fanned out in process, on the instance that committed the write. With several API
instances behind a load balancer, each streams the writes it handled and nothing else: a subscriber
connected to instance A does not see a publish that went through instance B. That is the known
limitation until a shared bus exists between instances; a single-instance deployment sees
everything. Content types running with `EventSourcing:DocumentTypesAppend` off write no events,
so nothing about them is streamed, the same way nothing about them fires a workflow.

## Stability and deprecation

There is no version segment in the URL and none is planned. The delivery API, meaning every route
under `/api/public`, follows the semantic version of the package that registers it instead. For the
routes above that is the core package. A module that adds a route under the same prefix, such as
`/api/public/files/{id}` from `BarakoCMS.Files` or `/api/public/{type}/semantic` from `BarakoCMS.AI`,
follows that module's own version under the same rules.

**What counts as breaking.** A route going away or changing shape, a response field being removed or
changing type, a filter or operator changing meaning, or a default changing (page size, sort order,
cache headers, what an omitted parameter means). Any of those lands only in a major version.

**What can land in a minor.** A new field, a new filter or operator, a new route, a new optional
parameter. A client that ignores what it does not know keeps working, so write clients that way.

**How a break is announced.** At least one minor release before the major that ships it, the change
is recorded in `CHANGELOG.md` under a **Delivery API** lead inside the entry, and the field or
behaviour is marked deprecated in this document. Until the major, the old behaviour keeps working
unchanged. So a consumer reading the changelog for each minor sees every break coming with at least
one release of notice, and a consumer who only reads this document sees it too.

**Why no version segment.** `/api/v1/public` and `/api/v2/public` means two code paths, two test
suites and two sets of projection rules to keep in step, for a project of this size. The change that
prompted the question, 3.20.0 making public delivery opt-in, would not have been helped by it: that
was a breaking change shipped in a minor with no notice, and a `v2` would have shipped the same
break to anyone who moved to it. What was missing was a written rule about when a break may ship and
how it is announced. This section is that rule.

**Security is the one exception.** A security fix ships in the next release whatever its number:
a hole in authentication, authorization, integrity or availability as much as a data exposure,
which is what 3.20.0 closed. It is still recorded under the same Delivery API lead and under
`### Breaking`, and it says what a consumer has to do.
