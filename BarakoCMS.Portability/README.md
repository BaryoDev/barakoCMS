<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.Portability logo" />
  <h1>BarakoCMS.Portability</h1>
  <p><em>Export your content as JSON, import it somewhere else.</em></p>
</div>

---

Exports content-type definitions and their content as one JSON bundle, and imports a bundle into
another instance. Useful for backups, moving between environments, seeding a new tenant, and sharing
content-type templates.

## Enable it

```sh
dotnet add package BarakoCMS.Portability
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=Portability`). Unset, every referenced module runs and the API logs
one warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.Portability.PortabilityModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.


## Endpoints

| Method & path | Purpose | Access |
|---|---|---|
| `GET  /api/portability/export` | Download a bundle | `Admin` / `SuperAdmin` |
| `POST /api/portability/import` | Apply a bundle | `Admin` / `SuperAdmin` |

## How an import behaves

- Content types are **upserted by name**, so re-importing an evolved bundle updates the type rather
  than duplicating it.
- Content is recreated **through events**, so imported content has real history and behaves
  identically to content authored in place.
- Each entry keeps the document-level sensitivity the bundle records for it, so a restored Hidden
  entry stays Hidden. A bundle from before 4.3.1 records none, and its entries import as Public.
- Each content type goes through the checks `POST /api/content-types` runs, its lifecycle
  included, and a stored type keeps the add field rule: a required field with no default is refused
  on a type that already has entries. An import does not change who may read a stored field; a
  bundle that raises, lowers or leaves out a non-Public field is refused, and
  `PUT /api/content-types/{name}/fields/{field}/sensitivity` is the way to change it.
- An import does not change the currency or scale a stored money field declares either. A bundle
  that carries a different one, or none where the stored field has one, is refused, and
  `PUT /api/content-types/{name}/fields/{field}/currency` is the way to change it. A field the
  stored type does not have yet takes what the bundle declares.
- A field's `editor`, `section` and `role` and a type's `routeTemplate` travel in the bundle and are
  checked the way `POST /api/content-types` checks them. Over a stored type, a value the bundle
  carries replaces the stored one and a member it does not carry is kept, so a bundle exported
  before these members existed leaves them as stored. A bundle cannot clear one;
  `PUT /api/content-types/{name}/fields/{field}/presentation` and
  `PUT /api/content-types/{name}/route-template` do.
- A type's `uniqueness` rules travel in the bundle. A type the import creates takes them, checked
  the way `POST /api/content-types` checks them. A stored type keeps its own: a bundle carrying none
  leaves them, and one carrying different rules, or leaving out a field a stored rule compares, is
  refused; `PUT /api/content-types/{name}/uniqueness` is the way to change them. An entry holding
  values a stored entry or an earlier entry of the bundle holds under a rule is refused by its
  index, unlike the singleton cap.
- Each entry goes through the same write path as `POST /api/contents`: a field the caller may not
  see is dropped, the entry is validated against its type as the bundle leaves it, the type's
  lifecycle hooks run, and the entry starts in the type's initial lifecycle state. The singleton cap
  is the one exception, since an import is a restore and lands what the bundle holds.
- The import is one database transaction, and each entry is written before the next is checked, so
  lifecycle hooks see the entries before it: journal entries are numbered in sequence, an account's
  parent and a page's parent from the same bundle resolve.
- Each record carries the `id` it had where it was exported. Import gives it a new id, and a
  reference field holding the old id of another record in the bundle is pointed at the new one,
  whatever order the records are in. A reference to anything outside the bundle must exist where
  the bundle is imported, so an entry referencing content left behind in another tenant is refused.
- A stored type keeps its lifecycle when the bundle has none. A bundle that changes it, or adds one
  to a type that already has entries, is refused.
- The entries' events are written together just before the commit, so a long import leaves no
  gap in the event sequence and workflows run for every imported entry. The rows the import writes
  are held for the whole transaction: run a large import outside busy hours, since a post that
  needs the same row (the journal entry number sequence, a content type the bundle changes) waits
  for it and may need a retry.
- All or nothing. A refused type or entry answers 400 naming each one as `contentTypes[i]` or
  `contents[i]`, and nothing is written, so the fixed bundle can be imported again without
  duplicating what would have landed. A dry run refuses exactly what the real run would.
- A bundle holds at most 5,000 entries (`Portability:MaxImportRecords`) and 500 content types.
  Past either, the import answers 400 before reading anything. Move a larger site in several
  bundles using export's `types` parameter.
- The import runs inside the calling tenant. A bundle carries no tenant identity of its own, which
  is what makes it safe to move between environments.

## Treat a bundle as sensitive

An export shows each entry the way the content read endpoints would show it to the caller who asked
for it. An entry the caller's read rule for its type does not allow is left out, the same entry
`GET /api/contents` leaves out, and so is an entry they may read nothing of; both are counted in
`contentsWithheld`. A field the caller may not read comes out under its mask and is listed in the
record's `maskedFields`.
Import skips every field named in `maskedFields`, so a mask is never stored as a value. A full
backup therefore needs a caller who may read every entry and every field, such as SuperAdmin. A
role holding `export_content` and no read rule for a type exports none of that type's entries.

What the caller may read is still in the bundle, and it leaves the system's access control behind
the moment it is downloaded.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under MPL-2.0.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
