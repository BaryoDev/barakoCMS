- **Listing entries by `contentType` ignores case.** Entries keep the type name as the caller
  spelled it, so `Article` and `article` entries of one type were two lists. `GET /api/contents?contentType=article`
  now returns both. The stored spelling is unchanged and is documented in `docs/content-type-names.md`.
