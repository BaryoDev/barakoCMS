- **No endpoint changed a field once it existed.** `POST /api/content-types/{name}/fields/{field}/change`
  classifies a change: a new `displayName` is additive and applied at once, and a `newName` is a
  rename applied by a job on the queue, which copies every entry's value to the new name, renames
  the field (and the type's uniqueness rules and transitions), then drops the old key, in batches
  that commit with their progress. `GET /api/content-types/field-changes/{id}` reports a change's
  step, progress and last error, and `POST .../{id}/resume` runs a stopped one again from where it
  stopped. A `toType` (with a `mapping` of stored values, and `options` for a choice) converts
  every value, and `remove` takes the field and its values away; a change that drops values is
  refused unless `allowDestructive` is set, and the refusal names how many entries hold one.
  Entry writes to the type wait while a change runs. The rewrite is a new
  `ContentFieldChanged` event, so it fires no workflow and does not move an entry's `updatedAt`.
  See `docs/field-changes.md`.
