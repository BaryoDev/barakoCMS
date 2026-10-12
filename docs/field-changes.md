# Changing a field

One endpoint changes a field of a type that may already hold entries, and says what kind of change
it was:

```
POST /api/content-types/{name}/fields/{field}/change
```

It needs `manage_content_types`. Send one change at a time.

| Body | Classification | Applied |
| :--- | :--- | :--- |
| `{ "displayName": "Attending" }` | `additive` | At once, 200. No entry changes |
| `{ "newName": "Practitioner" }` | `rename` | By a job, 202 |

A change that touches entries answers 202 with the job that applies it:

```json
{ "classification": "rename",
  "change": { "id": "...", "contentType": "clinic", "field": "Doctor", "kind": "rename",
    "state": "running", "step": 0, "steps": 3, "processed": 0, "total": 1200, "valuesLost": 0,
    "error": null, "startedAt": "...", "completedAt": null } }
```

## The job

The change is stored and queued in the same commit as the request, and a worker applies it in
batches of 200 entries. Each batch commits together with the progress it made, so a batch is
either done and recorded or neither.

A rename runs three steps: it copies every value to the new name while the field is still read
under its old one, renames the field on the type (and in the type's uniqueness rules and
transitions), then drops the old key. A field is read under its old name until the change
completes. Each entry's rewrite is a `ContentFieldChanged` event on its stream: it is not an edit,
so it fires no workflow, leaves `updatedAt` and the last editor alone, and appears in the entry's
history as `FieldChanged`.

While a change runs, entry writes to the type answer 400 naming the change, and a second change to
the type answers 409. A write landing between two batches could otherwise leave an entry in a
shape the job had already passed.

## Progress and resuming

| Route | Does |
| :--- | :--- |
| `GET /api/content-types/field-changes/{id}` | The change as it stands: its step, the entries it has rewritten, and the last error |
| `POST /api/content-types/field-changes/{id}/resume` | Queues a running change's job again, 202. A completed one is a 409 |

A job that fails is retried by the queue, and the error it hit is on the change until a batch
commits again. A job the queue gave up on stays `running`, and resuming it starts from the step
and entry it last recorded, so nothing done is done twice.
