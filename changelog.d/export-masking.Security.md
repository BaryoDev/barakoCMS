- **A portability export returned every entry, unmasked, whatever the caller could read.**
  `GET /api/portability/export` now applies the content List endpoint's per-entry read rule and the
  read endpoints' document and field sensitivity, keyed on the caller. An entry the caller may not
  read is left out and counted in the bundle's new `contentsWithheld`. A field the caller may not
  read comes out under its mask and is named in the record's new `maskedFields`, and import skips
  those fields, so a mask is never stored as a value. A caller needs a read rule for a type to
  export its entries, as on `GET /api/contents`; SuperAdmin still exports everything. A token whose
  user does not exist now gets 401, as on the List endpoint. BarakoCMS.Portability 4.3.1.
