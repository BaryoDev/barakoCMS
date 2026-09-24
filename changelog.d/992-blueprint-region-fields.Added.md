- **A tenant made from a blueprint could not set a page's HideTitle or the site's header and
  footer regions.** barakoPress reads them, but the blueprints did not declare them, so a kit had
  to add them with a script. `page` in `blog` and `devsite` now has an optional `HideTitle`, and
  `site` has `HeaderPath`, `FooterPath`, and `HeaderTone` and `FooterTone` as a choice of the
  renderer's six tones. Applying a blueprint never touches a type that already exists, so existing
  tenants are unchanged and add the fields by hand if they want them.
