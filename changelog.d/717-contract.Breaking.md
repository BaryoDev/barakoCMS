- **A write carrying a slug another entry of the type already holds is now refused with 400.** That
  tightens request validation on `POST /api/contents`, `PUT /api/contents/{id}`, the rollback route
  and bulk import, so `X-Api-Contract-Version` moves to 2. A deployment that already holds duplicate
  slugs is not rewritten: it keeps serving them until one of the entries is next edited, and that
  edit is refused until the slug on it is changed.
