- **Delivery describes an entry as schema.org JSON-LD.** A content type can declare a
  `structuredDataType` (`Article`, `NewsArticle`, `BlogPosting`, `Event`, `Product` or `WebPage`)
  on create or through `PUT /api/content-types/{name}/structured-data`, and a read by slug then
  carries `structuredData`, built from the fields holding the `title`, `summary`, `date`, `image`
  and `author` roles. `image` and `author` are new roles. The block is read off what delivery sends,
  so a field that is not Public, a file that is not public and a filtered reference never reach it.
  A type that declares nothing emits nothing. `GET /api/meta/describe` lists the values under
  `structuredDataTypes`, and the type description reports the type's own. See
  `docs/structured-data.md` (#567).
