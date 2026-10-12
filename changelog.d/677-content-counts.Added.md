- **An overview card had to page every entry to count them.** `GET /api/contents/counts` answers
  how many entries the caller may read by status (`draft`, `scheduled`, `published`, `archived`
  and the `total`), for one type or across every defined type, under the same read rules the entries
  list pages with. A type whose rules cannot be counted in a query is named in `uncountedTypes`
  instead of loaded row by row.
