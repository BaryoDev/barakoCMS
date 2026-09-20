- **The package contract was a list in a document, and nothing checked it.** `BarakoCMS.Abstractions`
  now holds the module interfaces, the documents and events, the service interfaces and the three
  workflow extension points, in an assembly that does not reference the core. A type reaching back
  into the host is a compile error instead of something review has to catch. Namespaces are
  unchanged, so no `using` moves and no module needs an edit; every module and the core reference
  the new package alongside what they referenced before.
