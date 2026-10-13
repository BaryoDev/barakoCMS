- **A public list cost two database round trips, and a build could not ask for only what changed.**
  `GET /api/public/{type}` now reads its page and `totalItems` in one query, and takes an optional
  `changedSince` instant that keeps the entries updated after it. Removals are not in a published
  list; learn of them from a webhook on `Unpublished` and `Deleted` or a periodic full fetch. A
  change to a type's fields or their sensitivity does not move `updatedAt`, so refetch the type in
  full after one. See `docs/delivery-api.md` (#508, #560).
