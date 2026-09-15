- **A content type with more than 200 fields is now refused with 400.** That tightens request
  validation on `POST /api/content-types`, `POST /api/content-types/{name}/fields`,
  `POST /api/content-types/{name}/seo-fields`, blueprint apply and
  `POST /api/portability/import`, so `X-Api-Contract-Version`
  moves to 4. The barakoBrew console refuses to start against a contract version it does not speak,
  so it has to be upgraded in lockstep with this release. The limit is `ContentTypes:MaxFields`.
  (#650)
