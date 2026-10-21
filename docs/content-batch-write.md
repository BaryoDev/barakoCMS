# Batch write

`POST /api/contents/batch` creates entries and runs lifecycle transitions in one database
transaction. It is part of the authoring API, so it needs a bearer token.

## Request

```json
{
  "steps": [
    { "op": "create", "contentType": "invoice", "id": "0198...", "data": { "title": "INV-1" } },
    { "op": "transition", "id": "0198...", "transition": "Issue" }
  ]
}
```

| Field | Step | Meaning |
| --- | --- | --- |
| `op` | both | `create` or `transition`, case does not matter. |
| `id` | both | Create: the id of the new entry, made by the client. Transition: the entry to move. Required. |
| `contentType` | create | The type of the new entry. Required. |
| `data` | both | Create: the field values. Transition: the values the transition declares. |
| `status` | create | A `ContentStatus` name. Draft when left out. |
| `sensitivity` | create | A `SensitivityLevel` name. Public when left out. |
| `transition` | transition | The name of the transition. Required. |

A transition may name an entry created by an earlier step of the same request. A transition of an
entry that is not stored and was not created earlier is a 404.

## Limits

- At least one step, at most 100. An empty list or more than 100 is a 400 and nothing runs.
- An unknown `op`, a missing `id`, `contentType` or `transition`, or a `status` or `sensitivity`
  that is not a valid name is a 400.
- A create id may appear once in a batch. A second create with the same id is a 400.

## What each step is held to

A step faces the checks its own request would: the create permission on the type, write
sensitivity, the create rule, the type's save rules, validation and hooks for a create; and the
read floor, the transition permission, the declared fields and the transition rules for a
transition. A creator still needs `Lifecycle:AllowSelfTransition:<Transition>` to move their own
entry. Each step is written inside the transaction before the next one is checked, so a rule or
hook in step 2 sees the entry step 1 made.

## Response

```json
{ "steps": [
  { "index": 0, "op": "create", "id": "0198...", "state": "Draft" },
  { "index": 1, "op": "transition", "id": "0198...", "state": "Issued" }
] }
```

`state` is the lifecycle state after the step, or `null` for a type with no lifecycle.

## Errors

The first refusal ends the run. The error is named for the step, `steps[<index>]`, with a zero based
index, and carries the reason:

```json
{ "errors": [ { "name": "steps[1]", "reason": "Id was already used in this batch." } ] }
```

| Status | Cause |
| --- | --- |
| 400 | Invalid step shape, an id used twice in the batch, or a refusal by validation. |
| 403 | A permission or create rule refused the step. The reason is `Forbidden.` with no detail. |
| 404 | A transition names an entry that does not exist. |
| 409 | A create names an id that is already stored (`Id is already in use.`), a uniqueness rule, or a transition conflict. |
| 422 | A save rule or transition rule refused the step. |

A step's rule detail goes only to a caller whose create rule holds for the entry. Anyone else gets
the 403.

## Atomic

A refusal rolls the whole batch back. Nothing from earlier steps is saved, and a client never holds
half of one. On success the events of the batch are appended last, and the transaction commits once.

## Idempotency and retry

`Idempotency-Key` works as on any other write (see `docs/idempotency.md`).

- A retry of a batch that was answered 200, with the same key and the same bytes, gets the stored
  answer back with `Idempotent-Replayed: true`. Nothing runs again.
- The same key with a different body is a 422.
- A batch that failed (any status outside 2xx) did not keep its key. The retry runs again, fails the
  same way if nothing changed, and still saves nothing.

Without a key, a retry of a batch that succeeded is a 409 on its first create, because that id is
now stored. Send a key.
