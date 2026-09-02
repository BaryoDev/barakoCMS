- **The admin no longer offers Editor a screen the API refuses.** `GET /api/content-types` stopped
  granting `Editor` when #373 landed, but the sidebar kept listing it, so the link rendered and the
  API answered 403. The test that should have caught it asserted the stale behaviour in its own name,
  "gives Editor the content types screen the API lets them reach", which is how it survived the
  server-side fix. It now asserts the general rule instead: a role the server has never heard of
  reaches no gated destination.
