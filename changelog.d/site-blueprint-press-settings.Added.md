- **The `site` blueprint declares the four settings barakoPress and barakoBrew read but could not
  be given.** `Currency` is the three-letter code the `money` binding format formats against, and
  without it a bound amount falls back to a plain number, because a default currency is one client's
  currency. `Space` and `Text` are the spacing and type scales a tenant overrides, which is what
  stops a theme from being pixel sizes written into blocks. `Presets` holds the saved blocks a
  designer builds in barakoBrew, which barakoPress reads and renders. Each is a field on the site
  singleton, so a deployment sets them per tenant with no release. Nothing changes for a site that
  leaves them empty: every one falls back to what the theme or the configuration already supplies.
  Additive, so `ApiContract.Version` does not move. (BaryoDev/barakoPress#33,
  BaryoDev/barakoBrew#140)
