- **A signed-in viewer can fetch an entry by its slug.** `GET /api/public/{type}/{slug}` is
  anonymous and serves only published, publicly deliverable entries, and the authenticated reads
  needed an id, so a page gated by a role could not be loaded from the slug in its URL.
  `GET /api/contents/by-slug/{type}/{slug}` applies what `GET /api/contents/{id}` applies to that
  entry: the same read permission check, tenant, statuses and field masking. An entry the caller
  may not read answers 404 rather than 403, since a slug can be guessed.
  Additive, so `ApiContract.Version` does not move. Needed by barakoPress viewer sessions
  (BaryoDev/barakoPress#7).
