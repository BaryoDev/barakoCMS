- **A portability import wrote types and entries that the create endpoints refuse (#933).**
  `POST /api/portability/import` now runs each content type through the checks
  `POST /api/content-types` runs, lifecycle included, and keeps a bundle's `lifecycle` on both new
  and updated types, where it used to drop it. On a stored type it refuses a required field with no
  default when the type has entries, and refuses a bundle that raises, lowers or leaves out a
  non-Public field; `PUT /api/content-types/{name}/fields/{field}/sensitivity` is how that changes.
  Each entry goes through the same write path as `POST /api/contents`, now shared as
  `IContentCreator`: a field the importing caller may not see is dropped, the entry is validated
  against its type as the bundle leaves it (required fields, value types, references, unique
  slugs), the type's lifecycle hooks run, and the entry starts in the type's initial lifecycle
  state. The import is all or nothing: any refusal answers 400 naming each item as
  `contentTypes[i]` or `contents[i]`, nothing is written, and a dry run answers the same. A bundle
  holds at most 5,000 entries (`Portability:MaxImportRecords`) and 500 types. A bundle that
  imported before and breaks one of these rules is now refused, so `ApiContract.Version` moves to 5,
  the same bump as the platform role change. `ISensitivityService` and `IContentValidatorService`
  gain an overload that takes the content type definition, with a default implementation that
  throws. BarakoCMS.Abstractions 4.5.0, BarakoCMS.Portability 4.4.0.
