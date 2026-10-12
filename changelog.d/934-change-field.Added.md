- **No endpoint changed a field once it existed.** `POST /api/content-types/{name}/fields/{field}/change`
  classifies a change: a new `displayName` is additive and applied at once, and a `newName` is a
  rename applied by a job on the queue, which copies every entry's value to the new name, renames
  the field (and the type's uniqueness rules, transitions, numbering rule and lookup), then drops the old key, in batches
  that commit with their progress. `GET /api/content-types/field-changes/{id}` reports a change's
  step, progress and last error, and `POST .../{id}/resume` runs a stopped one again from where it
  stopped. A `toType` (with a `mapping` of stored values, and `options` for a choice) converts
  every value, and `remove` takes the field and its values away; a change that drops values is
  refused unless `allowDestructive` is set, and the refusal names how many entries hold one.
  Only a Public field that holds no token is renamed, converted or removed. Entry writes to the
  type wait while a change runs, by every route, and edits to the type's definition answer 409
  until it completes. The rewrite is a new
  `ContentFieldChanged` event, so it fires no workflow and does not move an entry's `updatedAt`.
  See `docs/field-changes.md`.
