- **A public list cost two database round trips, and a build could not ask for only what changed.**
  `GET /api/public/{type}` now reads its page and `totalItems` in one query, and takes an optional
  `changedSince` instant that keeps the entries updated after it. Removals are not in a published
  list; read them from `GET /api/changes`. See `docs/delivery-api.md` (#508, #560).
