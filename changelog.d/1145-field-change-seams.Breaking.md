- **A field change left block bindings, and config apply, behind.** Renaming a field with
  `POST /api/content-types/{name}/fields/{field}/change` now renames it in every block binding of
  the type that sorts or filters on it, in the step that renames the field. Removing a field a
  binding still names answers 409 naming the bindings, where it used to succeed and leave the
  binding selecting nothing. `PUT /api/blocks/bindings/{id}` answers 409 while a field change runs
  on its type. `POST /api/config/apply` answers 409, with the refusal the `{name}` routes give, for
  a type with a field change running and for a document leaving out a field a binding names, and a
  plan lists them in a new `conflicts` list per section. It, and a Portability import, refuse a
  definition leaving out a field the stored numbering rule or lookup names. Each refusal is new to
  a request the API used to accept; `ApiContract.Version` already moved for 5.0.
