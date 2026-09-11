`POST /api/content-types/{name}/fields` adds one field to a content type that already exists. A
type could be created and read but never extended, so the only route to a new field was the SEO
endpoint, which does this same mutation for one hardcoded set. It refuses a name the type already
has rather than overwriting a field somebody else configured, refuses a required field with no
default on a type that already holds entries, and puts the merged field list through the same
validator `POST /api/content-types` uses. Additive, so the API contract version does not move.
