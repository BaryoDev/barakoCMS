- **A portability import wrote types and entries that the create endpoints refuse (#933).** `POST
  /api/portability/import` now runs each content type through the checks `POST /api/content-types`
  runs, lifecycle included, and refuses a field declared twice. A new type keeps the bundle's
  `lifecycle`, where it used to drop it. A stored type keeps its own when the bundle has none, and a
  bundle that changes it, or adds one to a type with entries, is refused. On a stored type the
  import refuses a required field with no default when the type has entries, and a bundle that
  raises, lowers or leaves out a non-Public field; `PUT
  /api/content-types/{name}/fields/{field}/sensitivity` is how that changes. A bundle type matches a
  stored type under create's name normalisation, so `Blog Post` updates `blog-post`. Each entry goes
  through the same write path as `POST /api/contents`, now shared as `IContentCreator`: a field the
  importing caller may not see is dropped, the entry is validated against its type as the bundle
  leaves it (required fields, value types, references, unique slugs), the type's lifecycle hooks
  run, and the entry starts in the type's initial lifecycle state. The import runs in one database
  transaction with each entry written before the next is checked, so validation and lifecycle hooks
  see the entries before it in the bundle: journal entries are numbered in sequence and a page's
  parent from the same bundle resolves. Export now writes each entry's `id`, and a reference to
  another record of the bundle is pointed at that record as imported, whatever the order. A
  reference to anything outside the bundle has to exist in the target. A type that an earlier import
  stored with no display name or with field names that are not PascalCase no longer imports until it
  is fixed. The import is all or nothing: any refusal answers 400 naming each item as
  `contentTypes[i]` or `contents[i]`, nothing is written, and a dry run answers the same. A bundle
  holds at most 5,000 entries (`Portability:MaxImportRecords`) and 500 types. A bundle that imported
  before and breaks one of these rules is now refused, so `ApiContract.Version` moves to 5, the same
  bump as the platform role change. `IContentBatchRunner` runs such a batch. `ISensitivityService`
  and `IContentValidatorService` gain an overload that takes the content type definition, with a
  default implementation that throws. BarakoCMS.Abstractions 4.5.0, BarakoCMS.Portability 4.4.0.
  `POST /api/import/content` moves onto the same path and the same transaction: a field the caller
  may not see is dropped instead of stored, the type's lifecycle hooks run and see earlier rows, and
  a request holds at most 5,000 records (`Import:MaxRecords`), refused with 400 past that. Its
  response shape and `continueOnError` are unchanged. BarakoCMS.Import 4.4.0.
