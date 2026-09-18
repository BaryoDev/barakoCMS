- **A deploy carrying a schema change the target database will not accept is now refused before the
  running container is replaced.** Production and playground run `AutoCreate.CreateOnly`, which
  creates a missing table and never alters an existing one, so a release carrying a delta on a table
  that is already there throws on start and crash-loops with the previous container already gone.
  `db-assert` was on the image and documented in the 4.0 upgrade guide, but nothing in the deploy
  path ran it. `scripts/assert-schema-current.sh` does, between the pull and the recreate. This is
  the second release to hit it: 3.14.0 on a new Marten index, and 4.2.0 on the `mt_doc_users` delta
  from the normalised identity work, after four images had been built and pushed. The production
  upgrade doc said "schema migrations run on start", which is true for a new table and not for a
  changed one; it now says which is which.
