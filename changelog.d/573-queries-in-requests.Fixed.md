- **A request definition can now use a query.** #328 closed a feature that refused itself: every
  `{{query.*}}` hole in a request's path, headers or body was refused with "queries are not
  implemented yet (#328)", whatever `RequestDefinition.QuerySlug` named, because nothing on the
  request path called `IQueryRunner`. A query could be defined, previewed and run through the API,
  and a request definition still could not use one.

  `{{query.rows}}` now composes to a JSON array of the named query's rows, one object per row,
  holding exactly the fields the query selects, bounded by its own `Limit` (itself capped at
  `QueryDefinition.MaxLimit`, 1000). It is inserted unescaped in a JSON body, since it is already
  valid JSON: quoting it would hand the recipient a string full of JSON instead of an array.
  `{{query.SomeField}}` composes to that field from the first row.

  The refusal is unchanged for a hole naming a query that does not exist or a field the query does
  not select: posting the literal text `{{query.rows}}` to a third party is worse than not running,
  and that has not stopped being true. A single field naming a query that matched no rows is
  refused too, rather than composing empty: "the query matched nothing" and "the field is
  genuinely empty" must not produce the identical value with nothing in the sent request to tell
  them apart afterwards. `{{query.rows}}` does not need this; an empty array is still a real
  answer to how many rows matched.

  A query is resolved through the same tenant-scoped session as everything else a request composes
  against, so a request never sees another tenant's query even when both hold the identical slug.
