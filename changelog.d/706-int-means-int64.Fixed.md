- **An int field takes any 64-bit integer on every input path.** A request body already did. Numeric
  text and a raw JSON number stopped at Int32, so the same value was accepted on one path and
  refused on another. A page's order in the Pages module now reads past Int32. A fraction or a
  number past Int64 is still refused with a 400 naming the field, and stored values read as before.
  Collection syncs are unchanged: a field declared `int` gets a whole number, and one declared
  `integer` or `number` keeps the cell text as it always has. See `docs/int-fields.md` (#706).
