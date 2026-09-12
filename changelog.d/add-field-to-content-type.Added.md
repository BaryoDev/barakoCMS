- **A content type's fields were fixed the moment it was created.** `POST /api/content-types/{name}/fields`
  adds one to a type that already exists. The only route to a new field was the SEO endpoint, which
  performs the same mutation for one hardcoded set, so a client asking for one more field on a type
  already holding their content had no answer that did not involve recreating the type. It refuses a
  name the type already has rather than overwriting it, refuses a required field with no default on a
  type that already has entries, and puts the merged field list through the same validator
  `POST /api/content-types` uses. Additive, so the API contract version does not move.
