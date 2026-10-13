# Changing a field

One endpoint changes a field of a type that may already hold entries, and says what kind of change
it was:

```
POST /api/content-types/{name}/fields/{field}/change
```

It needs `manage_content_types`. Send one change at a time; `displayName` is sent on its own.

Only a Public field is renamed, converted or removed, and a token field is not. Reads mask a field
by the name the type declares, and while a rename or a remove runs, and in every history event
written before it, the value sits under a name the type no longer declares, where nothing masks it.
Lower the field to Public first, knowing that is a disclosure of its own.

| Body | Classification | Applied |
| :--- | :--- | :--- |
| `{ "displayName": "Attending" }` | `additive` | At once, 200. No entry changes |
| `{ "newName": "Practitioner" }` | `rename` | By a job, 202 |
| `{ "toType": "choice", "options": [...], "mapping": { "Grade 1": "beginner" } }` | `convert`, or `destructive` when a value is dropped | By a job, 202 |
| `{ "remove": true, "allowDestructive": true }` | `destructive` | By a job, 202 |

A change that touches entries answers 202 with the job that applies it:

```json
{ "classification": "rename",
  "change": { "id": "...", "contentType": "clinic", "field": "Doctor", "kind": "rename",
    "state": "running", "step": 0, "steps": 3, "processed": 0, "total": 1200, "valuesLost": 0,
    "error": null, "startedAt": "...", "completedAt": null } }
```

## Convert and remove

A convert gives the field a new type: `string`, `text`, `markdown`, `int`, `decimal`, `bool`,
`date`, `datetime`, `email`, `url`, `slug` or `choice` (with `options`). A field holding one plain
value converts; a list, a file, a token, an object or a location does not. Each stored value is
read as text, looked up in `mapping` (up to 500 values), and kept when what comes out is a value of
the new type, an option for a choice. Anything else is dropped.

A change that drops stored values is destructive: a remove always, and a convert when some value
does not survive. It is refused unless the request says `"allowDestructive": true`, and the refusal
names the count: `Converting 'Grade' to choice drops the value 2 entries hold.` The count is
recorded on the change as `valuesLost`. A field a uniqueness rule, a transition, the numbering
rule or the lookup names is refused until it is taken out of them.

A convert rewrites every value, then retypes the field. A remove takes the field off the type
first, so nobody reads it, then drops every value.

## The job

The change is stored and queued in the same commit as the request, and a worker applies it in
batches of 200 entries. Each batch commits together with the progress it made, so a batch is
either done and recorded or neither.

A rename runs three steps: it copies every value to the new name while the field is still read
under its old one, renames the field on the type (and in the type's uniqueness rules, transitions,
numbering rule and lookup), then drops the old key. `processed` counts an entry once, when its old key
goes. A field is read under its old name until the change
completes. Each entry's rewrite is a `ContentFieldChanged` event on its stream: it is not an edit,
so it fires no workflow, leaves `updatedAt` and the last editor alone, and appears in the entry's
history as `FieldChanged`.

While a change runs, entry writes to the type answer 400 naming the change, by whichever route
they come: the API, a batch create or import, a workflow action or a sync. A second change to the
type answers 409, and so does every edit to the type's definition (a field's options, sensitivity,
currency or presentation, a new field, uniqueness, numbering, the lookup, public delivery, the route template,
structured data and SEO fields). A write landing between two batches could otherwise leave an
entry in a shape the job had already passed, and an edit storing the definition it read before the
change's definition step would undo that step.

## Progress and resuming

| Route | Does |
| :--- | :--- |
| `GET /api/content-types/field-changes/{id}` | The change as it stands: its step, the entries it has rewritten, and the last error |
| `POST /api/content-types/field-changes/{id}/resume` | Queues a running change's job again, 202. A completed one is a 409, and so is one whose job is still queued or running |

A job that fails is retried by the queue, and the error it hit is on the change until a batch
commits again. A job the queue gave up on stays `running`, and resuming it starts from the step
and entry it last recorded, so nothing done is done twice. Two runs of one change cannot both
commit a batch: the second finds the change moved on and its batch is rolled back.

A sensitivity change on a type of up to 200 entries runs inside its request, and its job is queued
with it to start a minute later, so a request cut short is finished by the job rather than leaving
the type's writes refused. There is no route to abandon a change yet.
