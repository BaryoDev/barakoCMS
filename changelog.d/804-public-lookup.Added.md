- **A public lookup by reference plus an identifier.** A content type can define a lookup
  (`PUT /api/content-types/{name}/lookup`, `manage_public_delivery`): a match field, verify fields
  and the Public fields to return, with optional reveal fields shown only while a bool on a
  singleton is true. `POST /api/public/{type}/lookup` answers a miss and a failed verification with
  the same empty 404, refuses to return a Sensitive, Hidden or token field, and is throttled per
  visitor (`RateLimiting:Lookup`) and per reference (`RateLimiting:LookupReference`). See
  `docs/public-lookup.md` (#804).
