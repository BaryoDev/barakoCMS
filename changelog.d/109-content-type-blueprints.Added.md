- **Content type blueprints: a site starts from a named set of types instead of an empty schema.**
  `GET /api/content-types/blueprints` lists them and `POST /api/content-types/blueprints/{name}`
  creates every type one declares in the caller's tenant. Four are built in: `blog` (post, category,
  author, page), `events` (event, venue, speaker, with a geopoint location), `portfolio` (project,
  client) and `docs` (article, section). Every addressable type has a slug field, and fields that are
  for the team rather than the public are marked Sensitive or Hidden.

  Applying is additive and all or nothing: a type that already exists refuses the whole blueprint
  with a 409 naming the clash, and types the blueprint does not mention are left alone. Gated on
  `manage_content_types`, like the create it stands in for.

  Set `Blueprints:Path` to a directory and its `*.json` files are listed alongside the built-ins.
  Each file is validated when listed, with the same validator the create endpoint runs, and a broken
  file shows its errors in the list rather than failing at apply time.
