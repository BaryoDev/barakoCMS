- **A tenant made from the `site` blueprint could not edit the newer barakoPress settings in
  barakoBrew.** barakoPress 0.8.0 reads them, but the blueprint did not declare them, and public
  delivery sends only declared fields. `site` now has optional json fields `Tokens`, `Tones`,
  `StyleRecipes`, `MenuLinks`, `HeaderActions` and `Plugins`. Applying a blueprint never touches a
  type that already exists, so existing tenants are unchanged and add the fields by hand if they
  want them.
