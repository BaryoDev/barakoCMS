- **A workflow template could not reach a referenced entry or list related ones.**
  `{{data.Supplier.Email}}` now reads a field of the entry a `reference` field points at, and
  `{{#each data.Runners}}...{{/each}}` renders its text once per entry a reference field points at.
  What renders is what the user who fired the workflow may read, checked with the same permission
  and sensitivity services as `GET /api/contents/{id}`: an entry they may not read renders empty,
  like a missing one, and so does a Sensitive or Hidden field they may not see. References are
  followed one level deep with a fixed number of reads per action. A loop renders the entries among
  the first 50 ids and stops, and the attempt's `error` on a succeeded attempt (or the action's
  `errorMessage` in the execution log) says how many the field held. A dry run does not follow
  references. Saving a workflow warns about a loop inside a loop (#828, #805).
