- **Delivery uses a slug field only when the type marks it Public.** A field of type `slug` marked
  Sensitive is not the type's slug on the anonymous routes: it is not served as the top-level
  `slug`, `GET /api/public/{type}/{slug}` does not look entries up by it, and the feed and sitemap do
  not build links from it. The type then has no slug route, as a type with no slug field never had.
  Signed-in reads by slug, the uniqueness check on write and collection pushes still use the field.
