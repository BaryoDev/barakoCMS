- **A collection sync could only copy a value the item already held.** A roadmap needing a
  product name, a repository taken out of a URL, every label of an issue or a percent complete had
  to be filled by an import script run by hand. A sync definition now takes `fieldRules` beside
  `fieldMap`: a constant, a path with `prefixStrip`, `regex` or `join`, a `contains` test over an
  array path, and a `ratio` of closed over closed plus open as a rounded percent. `exclude` skips
  an item when a path is not empty or equals a value. Every rule is checked on save with a 400
  naming the field. Both properties are optional, and a definition without them runs as before.
  See docs/collection-syncs.md.
- **A sync could not take an entry off the page when its source dropped it.** A closed milestone
  or an issue somebody took stayed published. `archiveMissing: true` archives the published entries
  a sync owns that a complete run did not produce, and publishes one again if it comes back. It
  acts only when the run read everything: no `maxEntries` cut, no next page in the `Link` header,
  no skipped item. Entries typed by hand or written by another sync are never touched. Off by
  default, and the run result reports `archived`.
