- **Listing entries by `contentType` ignores case.** Entries keep the type name as the caller
  spelled it, so `Article` and `article` entries of one type were two lists. `GET /api/contents?contentType=article`
  now returns both. The stored spelling is unchanged and is documented in `docs/content-type-names.md`.
- **A role's permission for a content type applies whatever the case of the entry's `contentType`.**
  Without it, the list found an `Article` entry through a rule for `article` and then hid it.
  A role that holds two spellings of a type uses the exact one first.
