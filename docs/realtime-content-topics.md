# Realtime topics on a content type

A content type can name a realtime topic. A client subscribes to it on the hub and reads the change
feed for it, the same two pieces as a module topic (`docs/realtime.md`), with the read permission on
the type taking the place of a capability.

## Declaring one

Set `RealtimeTopic` on the content type, in the type's config document (`POST /api/config/apply`):

```json
{ "displayName": "Ticket", "realtimeTopic": "desk.tickets", "branchScoped": true }
```

The apply is refused with 400 when:

1. the name is not lower case words joined by dots or dashes
   (`Realtime topic '...' must be lower case words joined by dots or dashes.`),
2. a module already owns the name (`Realtime topic '...' is used by module '...'.`), or
3. another content type of the tenant holds it (`Realtime topic '...' is already used by content type '...'.`).
   Two types of one config document are refused the same way. A type may keep the topic it holds.

The same name can exist in two tenants. A saved, changed or removed topic is seen by the next
subscribe or notice on the writing instance without a restart.

## Subscribing

`Subscribe(topic)` on `/hubs/realtime`, with the same token checks as a module topic.

- The caller needs the `read` permission on the type. Without it the hub throws
  `This caller cannot read '<type>'.`
- A type that is not branch scoped has one group, and every subscriber hears every notice.
- A branch scoped type has one group per branch. The caller joins the groups of the branches they
  hold when they subscribe, and a notice goes to the group of the entry's branch. An entry with no
  branch notifies no one. A caller who holds no branch is refused (`This caller holds no branch.`).
- Rule conditions on the read permission are not run at the hub. The feed runs them per entry.

## What a notice and a feed item say

A notice is `{ topic, sequence, streamId, type }`. A feed item is the usual `sequence`, `streamId`,
`type`, `topics` and `timestamp`, with `data`:

```json
{ "contentType": "ticket", "id": "0198...", "lifecycleState": "Called", "branchId": "0198..." }
```

Ids and the lifecycle state. No field values. The state is the entry's state now, not at the event,
so read the entry with `GET /api/contents/{id}` for the rest. The topic carries these content
events: created, updated, status changed, transitioned, field changed, sensitivity changed and field
sensitivity changed.

## Reading the feed

`GET /api/changes?after=<sequence>&topics=<topic>`

- Name one content topic and nothing else. Combined with another topic it is a 400
  (`A content topic cannot be combined with other topics.`).
- The caller needs `read` on the type, else 403 (`This caller cannot read '<type>'.`).
- Each entry is checked as the content GET checks it: the read permission with its rule conditions,
  the branch for a branch scoped type (an entry outside the caller's branches is left out), and a
  hidden entry is left out.
- `limit` is 1 to 1000, default 200. `next` is the cursor to send as `after`. The feed never reads
  past the high-water mark.
- One request looks through at most 10 pages of raw events. When the type's events are rare among
  the others, a page can come back with fewer items than `limit`, or none, and a `next` below the
  high-water mark. Continue from `next`.

## lifecycleState on reads

`GET /api/contents/{id}`, `GET` by slug and the list return `lifecycleState`: the stored state, or
the type's initial state for an entry not yet moved. It is `null` for a type with no lifecycle and
for an entry shown as hidden.

## Limit

Notices reach only connections on the instance that wrote, as for module topics. A client that
reconnects reads the feed from its last cursor, so a missed notice delays a refresh and loses
nothing.
