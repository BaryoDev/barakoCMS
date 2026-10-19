- **Site `Mode` can be `Private`.** While it is set, every `/api/public/*` route of that tenant
  answers 404 unless the request carries a link to the whole site in `X-Share-Key` or comes from a
  signed-in viewer who may read the `site` type. `GET /api/tenants/{handle}/public` follows the same
  rule. Another tenant is unaffected, and a tenant with no `Mode` is `Live`. This is opt-in per
  tenant, so no existing route changes unless a site is set to `Private`, and neither contract
  number moves. A `site` type made before this release gets the option with `set-field-options` on
  `Mode`. See `docs/site-settings.md`.
