# Event envelope: branch, node and origin sequence

Since 5.1 every content event carries headers that say where it was written. A host that runs on
more than one machine (a site server and its branches, say) uses them to tell its own events from
another node's and to keep each branch's rows apart.

## Headers

| Header | Value | Set when |
|---|---|---|
| `branch-id` | the request's branch id | the request sent `X-Branch-Id` |
| `origin-node-id` | this node's id | `Barako:NodeId` is set |
| `origin-sequence` | a number, gapless and increasing per node | `Barako:NodeId` is set |

The origin sequence is the number series `origin.{nodeId:N}`. Two events in one save get
consecutive numbers in stream order, and a save that fails uses none. An event that already carries
`origin-sequence` when it is saved keeps it.

The number is taken from the series row with a row lock held until the save commits. Saves of one
node that carry an origin sequence therefore run one after another. That is what keeps the numbers
gapless, and it caps the write rate of one node at one commit at a time for those saves. Give each
node its own `Barako:NodeId`; do not share one id between nodes.

Set the node id once per install, as a Guid:

```json
{ "Barako": { "NodeId": "0192f0c4-6a1e-7b51-9a30-2f1c8d4e5a77" } }
```

Without it no node or sequence header is written. The branch header does not need it.

## X-Branch-Id

A request names its branch with `X-Branch-Id: <branch id>`. The id must be one of the branches in
the caller's `branches` member profile attribute, a comma separated list of up to 32 ids, set like
any other profile attribute by `POST /api/tenants/members` or `PUT /api/tenants/members/{userId}`.
A branch not in that list is refused with 403. A malformed header is refused with 400.

The short code a `{branch}` series template uses is read from the branch entry. By default that is
the `Code` field of the `Branch` content type. `Envelope:BranchContentType` and
`Envelope:BranchCodeField` name other ones.

## Branch scoped types

A content type with `"BranchScoped": true` belongs to branches. A create without `X-Branch-Id` is
refused with 400 and the text `A branch is required.` Each entry keeps its branch in
`Content.BranchId`, which a projection rebuild restores from the first event's `branch-id` header.

A caller reaches an entry of such a type only when the entry has a branch and the caller holds it in
their `branches` profile attribute. The rule is the same on every route:

- `GET /api/contents/{id}`, `GET /api/contents/by-slug`, history, rollback, runs, schedule, update,
  status change and erase answer 404 for an entry of a branch the caller does not hold, as for an id
  that does not exist.
- `GET /api/contents` and `GET /api/contents/counts` leave those entries out, and the page and the
  totals are taken from what is left.
- Each step of `POST /api/contents/batch` is refused like its single request, with 404 for a
  transition of such an entry, and the batch saves nothing.
- The change feed and realtime apply the same rule.

An entry with no branch is hidden from every caller of a branch scoped type. A type that is not
branch scoped is not filtered. Access rules with `$CURRENT_BRANCH` (below) apply on top of this.

## Merge class

`ContentTypeDefinition.MergeClass` tells a host how edits made on two nodes are merged when they
sync: `Append`, `FieldLww` or `OwnedRequest`. Core stores it and does not act on it. Null leaves the
choice to the host.

## Access rules

A rule reads the entry's branch as the field `$branch` and the request's branch as
`$CURRENT_BRANCH`:

```json
{ "Read": { "Enabled": true, "Conditions": { "$branch": { "_eq": "$CURRENT_BRANCH" } } } }
```

With no header, or a branch the caller does not hold, the variable has no value and the rule
matches nothing. An entry with no branch does not match `_eq` and does match `_ne`. The details are
in [access-control.md](access-control.md#the-requests-branch).

The profile name `branch` is reserved for the request's branch. A member write refuses it, and a
stored one from before 5.1 is ignored.

## Migration

Apply `migrations/5.1.0/event-headers.sql` before deploying 5.1. It adds the nullable
`mt_events.headers` column and replaces `mt_quick_append_events`. It is safe to run twice and safe
to apply while 5.0 is still serving. `migrations/5.1.0/rollback-event-headers.sql` undoes it.

```bash
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.1.0/event-headers.sql
```
