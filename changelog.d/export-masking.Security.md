- **A portability export returned every entry's data unmasked.** `GET /api/portability/export`
  now applies the same document and field sensitivity as the content read endpoints, keyed on the
  caller. A field the caller may not read comes out under its mask and is named in the record's new
  `maskedFields`; an entry the caller may read nothing of is left out and counted in the bundle's new
  `contentsWithheld`. Import skips the fields a record lists in `maskedFields`, so a mask is never
  stored as a value. An Admin without the role a field names now gets that field masked, as on the
  read endpoints; SuperAdmin still exports everything. BarakoCMS.Portability 4.3.1.
