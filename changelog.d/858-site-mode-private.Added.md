- **Site `Mode` can be `Private`.** While it is set, every `/api/public/*` route of that tenant
  answers 404 unless the request carries a link to the whole site in `X-Share-Key` or comes from a
  signed-in viewer who may read the `site` type. Another tenant is unaffected. A `site` type made
  before this release gets the option with `set-field-options` on `Mode`. See
  `docs/site-settings.md`.
