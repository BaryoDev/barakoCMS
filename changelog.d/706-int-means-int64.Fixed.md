- **An int field takes any 64-bit integer on every input path.** A request body already did. Numeric
  text and a raw JSON number stopped at Int32, so the same value was accepted on one path and
  refused on another. Collection syncs and their sum, ratio and floor rules now treat `integer` and
  `number` as `int`, and a page's order in the Pages module reads past Int32. A fraction or a number
  past Int64 is still refused with a 400 naming the field, and stored values read as before. See
  `docs/int-fields.md` (#706).
