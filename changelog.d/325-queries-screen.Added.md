- **The queries screen.** `/api/queries` had no interface, so a saved query could only be created by
  hand against the API. The admin now lists, builds, previews and deletes them under Queries.

  The form offers exactly the shape the model accepts and no more: a content type, up to ten typed
  filters, a sort, a limit and an explicit field projection. There is nowhere to type an expression,
  because there is nowhere in the model to put one. Only fields the content type marks Public are
  offered to filter on, sort by or return, which is the same allowlist the runner enforces and for
  the same reason: filtering on a field the rows cannot show is a way to read that field without
  ever printing it.

  The preview is the part that makes it useful. Pressing it saves any pending edits and then runs the
  stored definition, and the rows come back as a table of the projected fields in the order the
  projection names them. That is what a workflow action carrying the query would send. A run the
  server refuses shows the server's own reason, so a field raised to Sensitive after the query was
  written surfaces here rather than in a payload.
