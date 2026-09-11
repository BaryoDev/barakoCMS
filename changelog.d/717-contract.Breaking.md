- **A write carrying a slug another entry of the type already holds is now refused with 400.** That
  tightens request validation on `POST /api/contents`, `PUT /api/contents/{id}`, the rollback route
  and bulk import, so `X-Api-Contract-Version` moves to 2. The barakoBrew console refuses to start
  against a contract version it does not speak, so it has to be upgraded in lockstep with this
  release, not after it. A deployment that already holds duplicate slugs is not rewritten: it keeps
  serving them, the slug route now answers with the oldest of them every time rather than whichever
  one Postgres handed back first, and the next edit of one of the colliding entries is refused until
  the slug on it is changed. One caveat on bulk import: a row colliding with a stored entry is
  refused, but two rows of the same batch carrying the same slug as each other are still both
  accepted.
