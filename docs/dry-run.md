# Dry run

Four writes take `"dryRun": true` in their body:

| Route | A dry run answers |
| :--- | :--- |
| `POST /api/contents` | `dryRun: true` and `changes`: every field the entry would hold |
| `PUT /api/contents/{id}` | `dryRun: true` and `changes`: each field whose value would change, with `from` and `to` |
| `POST /api/content-types` | `dryRun: true` and `changes`: each field the type would have, `to` its type |
| `POST /api/content-types/{name}/fields` | `dryRun: true`, `fields` as the type would list them, and the one field in `changes` |

A dry run runs, in the same order as the real write: authorization, field permissions and
sensitivity, schema validation and the type's lifecycle hooks. A request those would refuse is
refused the same way, with the same status and errors. A request they accept answers 200 with
what it would change, and nothing is stored: no entry, no event, no type, no audit entry. An
`Idempotency-Key` sent with a dry run is released afterwards, so the real write can send it.

Three checks run only when the write is stored, so a dry run that answers 200 can still be
followed by a refused write:

- **Uniqueness rules.** They are enforced as the entry is stored, against what is stored then.
- **The version an update was read at.** `If-Match` (412, or 428 where it is required) and an
  event-sourced type's expected version (409) are compared at the write.
- **A field change running on the type.** Entry writes to the type wait for it (400).

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
