# Dry run

Four writes take `"dryRun": true` in their body:

| Route | A dry run answers |
| :--- | :--- |
| `POST /api/contents` | `dryRun: true` and `changes`: every field the entry would hold |
| `PUT /api/contents/{id}` | `dryRun: true` and `changes`: each field whose value would change, with `from` and `to` |
| `POST /api/content-types` | `dryRun: true` and `changes`: each field the type would have, `to` its type |
| `POST /api/content-types/{name}/fields` | `dryRun: true`, `fields` as the type would list them, and the one field in `changes` |

A dry run runs everything the real write runs, in the same order: authorization, field
permissions and sensitivity, schema validation, the type's lifecycle hooks, and the conflict
checks. A request the real write would refuse is refused the same way, with the same status and
errors. A request it would accept answers 200 with what it would change, and nothing is stored: no
entry, no event, no type, no audit entry.

```json
{ "id": "...", "version": 0, "dryRun": true,
  "changes": [ { "field": "Grade", "from": 3, "to": 4 } ] }
```

A field the write leaves out is listed with `to` null. A field the server generates, such as a
token, is not in a create's `changes`: its value is made when the entry is stored.

The checks that read other entries (uniqueness, a reference's target, a slug's owner) read them as
they are when the dry run runs, so a real write sent later can still be refused if someone wrote in
between.
