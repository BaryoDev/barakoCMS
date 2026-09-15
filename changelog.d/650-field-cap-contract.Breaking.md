- **A content type with more than 200 fields is now refused with 400.** That tightens request
  validation on `POST /api/content-types`, `POST /api/content-types/{name}/fields`,
  `POST /api/content-types/{name}/seo-fields`, blueprint apply and
  `POST /api/portability/import`. It shares the move of `X-Api-Contract-Version` to 4 with the
  locked-account status change (#640), so contract 4 covers both breaks. The barakoBrew console
  refuses to start against a contract version it does not speak, so it has to be upgraded in
  lockstep with this release. The limit is `ContentTypes:MaxFields`. (#650)
