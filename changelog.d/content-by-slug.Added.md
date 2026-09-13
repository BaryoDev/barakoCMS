- **A signed-in viewer can fetch an entry by its slug.** `GET /api/public/{type}/{slug}` is
  anonymous and serves only published, publicly deliverable entries, and the authenticated reads
  needed an id, so a page gated by a role could not be loaded from the slug in its URL.
  `GET /api/contents/by-slug/{type}/{slug}` answers exactly what `GET /api/contents/{id}` answers
  for that entry: the same read permission check, tenant, statuses, 401/403/404 and field masking.
  Additive, so `ApiContract.Version` does not move. Needed by barakoPress viewer sessions
  (BaryoDev/barakoPress#7).
