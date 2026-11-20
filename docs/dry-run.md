# Dry run

Seven writes take `"dryRun": true` in their body:

| Route | A dry run answers |
| :--- | :--- |
| `POST /api/contents` | `dryRun: true` and `changes`: every field the entry would hold |
| `PUT /api/contents/{id}` | `dryRun: true` and `changes`: each field whose value would change, with `from` and `to` |
| `POST /api/content-types` | `dryRun: true` and `changes`: each field the type would have, `to` its type |
| `POST /api/content-types/{name}/fields` | `dryRun: true`, `fields` as the type would list them, and the one field in `changes` |
| `POST /api/content-types/{name}/fields/{field}/change` | `dryRun: true`, the `classification`, and `changes`: the field's name, type or label before and after. A convert or remove also has `valuesLost`. No job is started |
| `PUT /api/content-types/{name}/lifecycle` | `dryRun: true`, `canApply`, the proposed `lifecycle`, and every `blockers` and `warnings` entry the real edit would answer 409 with. A dry run that is blocked still answers 200. It needs no `If-Match`, and a stale one it sends is a 412 |
| `POST /api/contents/batch` | `dryRun: true` and each step as it would run; a create step carries `changes`. The batch runs in its transaction and is rolled back |

A dry run runs, in the same order as the real write: authorization, field permissions and
sensitivity, schema validation and the type's lifecycle hooks. A request those would refuse is
refused the same way, with the same status and errors. A request they accept answers 200 with
what it would change, and nothing is stored: no entry, no event, no type, no field change job, no audit entry. An
`Idempotency-Key` sent with a dry run is released afterwards, so the real write can send it.

Some checks run only when the write is stored, so a dry run that answers 200 can still be
followed by a refused write. What each dry-run write leaves out:

- **Entry create and update.** Uniqueness rules are enforced as the entry is stored, against what
  is stored then. An update's `If-Match` (412, or 428 where it is required) and an event-sourced
  type's expected version (409) are compared at the write. A field change running on the type makes
  the real write wait (400); the dry run does not check it.
- **Batch.** Uniqueness runs at each create step, as the entry is staged, and a field change
  running on the type is refused (400), as in the real batch. A create step takes no number: its
  numbering field is left out of `changes`, and the series' counter does not move. The final commit
  phase does not run: the events the steps would append are not written, so a batch that could fail
  only there (the deferred event appends or the last save) answers 200 as a dry run. Running that
  phase and rolling back would burn event sequence numbers, and a gap in them delays the async
  daemon until it passes its stale threshold.
- **Field change.** A rename, convert or remove checks for a change already running on the type
  (409), as the real call does. No job is started, no audit entry is written and nothing is stored.
  A label change has no such check, as the real call has none.
- **Lifecycle edit.** Runs the real edit's checks and counts, and answers 200 with the `blockers`
  the real edit would refuse with, one for each entry state, initial state, role, workflow trigger,
  workflow action, workflow run, rule, uniqueness rule and schedule that stands in the way, and a
  `templateLink` warning for each email link to a transition the edit takes away. The check that the type did not change between the counts and
  the save runs only when the edit is stored.

```json
{ "id": "...", "version": 0, "dryRun": true,
  "changes": [ { "field": "Grade", "from": 3, "to": 4 } ] }
```

A field the write leaves out is listed with `to` null. An update's `from` and `to` are the values
as the caller's `GET` of the entry shows them, masked where it masks them, and a field it does not
show is left out. A caller whose role may update the entry but not read it is told only what it
sent: every field, with `from` null. A field the server generates, such as a
token, is not in a create's `changes`: its value is made when the entry is stored.

The checks that read other entries (a reference's target, a slug's owner) read them as
they are when the dry run runs, so a real write sent later can still be refused if someone wrote in
between.
