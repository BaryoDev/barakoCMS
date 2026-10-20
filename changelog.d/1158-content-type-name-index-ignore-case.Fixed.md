- **Two content types whose names differ only by case could be stored.** The unique index on the
  name was exact-case while every lookup ignores case, so such a pair made both spellings fail as
  ambiguous. The index is on the lowercased name now. An existing database needs
  `migrations/5.1.0/1158-content-type-name-index-ignore-case.sql`, which refuses and lists the
  clashing names if there are any.
