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
  "change": { "id": "...", "contentType": "shop", "field": "Owner", "kind": "rename",
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

## Block bindings

A [block binding](blocks.md#presets-and-bindings) names fields of its type in its `sort` and its
`where` conditions, and a binding naming a field the type does not declare selects nothing, so a
page showing it empties with no error. A field change keeps them in step:

- A rename carries every binding of the type that names the field to the new name, a descending
  `sort` keeping its `-`, in the same commit as the step that renames the field on the type. Only
  the bindings of the tenant the type belongs to are touched.
- A remove is refused with 409 while a binding still names the field, and the refusal names each
  binding by name and id. Change the binding or delete it, then remove the field. Refusing was
  chosen over removing the condition or the sort for you: either would change what a published
  page shows, which is a decision for whoever owns the page.
- While a change runs on a type, saving a binding on it answers 409 too, so no binding is saved
  around the step that renames the field.

`POST /api/config/apply` follows the same rules: a document leaving out a field a binding names is
refused with 409, and so is a document changing a type while a field change runs on it.

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

## Recomputing a formula

Saving a type with a new or changed computed `expression`, while the type has entries, starts a
field change of kind `recompute` in the same commit as the type. A worker works the value out again
for every entry in batches, the same way as the other kinds. Each rewrite is a
`ContentFieldChanged` event, so it is not an edit and fires no workflow. A change to an `fn` rule
starts no job.

Adding a computed field with a formula to a type that has entries, `POST /api/config/apply` and a
bundle import all start it. The first two answer 409 when another field change is running on the
type. A bundle import starts it after the bundle's entries are written, and only when nothing in
the bundle was refused. Adding a formula field to an account's content type starts one change in
each tenant of the account that holds entries of the type. See [computed-fields.md](computed-fields.md).

Removing a formula from a field, or changing it to an `fn` rule, starts no recompute. The values
already stored stay as they are. A later write to an entry works the value out by the `fn` rule,
if the field has one.

## Progress and resuming

| Route | Does |
| :--- | :--- |
| `GET /api/content-types/field-changes/{id}` | The change as it stands: its step, the entries it has rewritten, and the last error |
| `POST /api/content-types/field-changes/{id}/resume` | Queues a running change's job again, 202. A completed or abandoned one is a 409, and so is one whose job is still queued or running |
| `POST /api/content-types/field-changes/{id}/abandon` | Stops a running recompute and frees the type, 200. Any other kind, a completed or abandoned change, and one whose job is still queued or running are a 409 |

A job that fails is retried by the queue, and the error it hit is on the change until a batch
commits again. A job the queue gave up on stays `running`, and resuming it starts from the step
and entry it last recorded, so nothing done is done twice. Two runs of one change cannot both
commit a batch: the second finds the change moved on and its batch is rolled back.

A sensitivity change on a type of up to 200 entries runs inside its request, and its job is queued
with it to start a minute later, so a request cut short is finished by the job rather than leaving
the type's writes refused.

## Abandoning a recompute

A change whose job keeps failing on one entry fails every resume, and while it is `running` the
type refuses writes and edits. Abandoning a recompute is the way out. The change takes the state
`abandoned`, with `abandonedAt` and `abandonedBy` (the caller's user id), the type is free again,
and the job does nothing if it runs later. It needs `manage_content_types`, and it is recorded in
the audit log as `contenttype.field_change.abandoned` with the change's id, type, field and
counts. No entry value is recorded.

Only a recompute can be abandoned, because stopping one part way leaves nothing wrong. An entry the
job has not reached keeps its old computed value, and the next write to it works the value out
again from the current formula. The other kinds leave state a stop cannot undo: a rename leaves
values under two names, a convert leaves entries of mixed types, and a remove leaves a field's
values that come back when the name is added again. Those answer 409 with "Only a recompute can be
abandoned. Resume this change instead." and are resumed until they finish.

A change that completed or was already abandoned is a 409. So is one whose job is still queued or
running, since abandoning under a live job would race it. Abandon it once the queue has given up
on the job. Another tenant's change is a 404. An abandoned change is not resumed.
