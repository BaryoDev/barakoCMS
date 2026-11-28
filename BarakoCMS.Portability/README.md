<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.Portability logo" />
  <h1>BarakoCMS.Portability</h1>
  <p><em>Export your content as JSON, apply it somewhere else.</em></p>
</div>

---

Adds the `contents` section to the config engine, so content-type definitions and their entries
move between instances as one JSON document. Useful for backups, moving between environments,
seeding a new tenant, and sharing content-type templates.

`GET /api/config/export?sections=contentTypes,contents` writes the document, and
`POST /api/config/plan` and `POST /api/config/apply` read it. The module has no routes of its own:
the two routes it had under `/api/portability` were removed in 6.0, as announced in 4.6.0. A
document exported by the old route (`{ "version": 1, "contentTypes": [...], "contents": [...] }`)
is still accepted by plan and apply.

`?types=` narrows the export to the content types you name, for example
`GET /api/config/export?sections=contentTypes,contents&types=article,author`. It is a comma list,
matched without regard to case. It narrows `contentTypes` and `contents` only, so another section
in the same request is exported whole. A name that matches no type is ignored, and with no `types`
every type is exported. For entries the filter runs in the database, before the limit below.

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


## Routes

The module registers the `contents` section. The routes are the config routes, gated on
`manage_config`.

| Method & path | Purpose | Access |
|---|---|---|
| `GET  /api/config/export?sections=contentTypes,contents` | Download the document | `manage_config`, `export_content` for the entries |
| `POST /api/config/plan` | See what an apply would do | `manage_config`, `export_content` or `import_content` for the entries |
| `POST /api/config/apply` | Apply a document | `manage_config`, `import_content` for the entries |

The engine asks for the read and the write capability of every section a document holds, with one
exception. A plan of `contents` holds only the document the caller sent, so a plan takes either
`export_content` or `import_content`, and an apply takes `import_content` alone. A role that only
imports needs no `export_content`. A document that carries `contentTypes` needs
`manage_content_types`. `Admin` and `SuperAdmin` hold all of them.

## How an apply behaves

- Content types are **upserted by name**, so applying an evolved document updates the type rather
  than duplicating it.
- Content is recreated **through events**, so applied content has real history and behaves
  identically to content authored in place.
- Each entry keeps the document-level sensitivity the document records for it, so a restored Hidden
  entry stays Hidden. A document from before 4.3.1 records none, and its entries apply as Public.
- Each content type goes through the checks `POST /api/content-types` runs, its lifecycle
  included, and a stored type keeps the add field rule: a required field with no default is refused
  on a type that already has entries. An apply does not change who may read a stored field; a
  document that raises, lowers or leaves out a non-Public field is refused, and
  `PUT /api/content-types/{name}/fields/{field}/sensitivity` is the way to change it.
- An apply does not change the currency or scale a stored money field declares either. A document
  that carries a different one, or none where the stored field has one, is refused, and
  `PUT /api/content-types/{name}/fields/{field}/currency` is the way to change it. A field the
  stored type does not have yet takes what the document declares.
- A field's `editor`, `section` and `role` and a type's `routeTemplate` travel in the document and
  are checked the way `POST /api/content-types` checks them. Over a stored type, a value the
  document carries replaces the stored one and a member it does not carry is kept, so a document
  exported before these members existed leaves them as stored. A document cannot clear one;
  `PUT /api/content-types/{name}/fields/{field}/presentation` and
  `PUT /api/content-types/{name}/route-template` do.
- A type's `uniqueness` rules travel in the document. A type the apply creates takes them, checked
  the way `POST /api/content-types` checks them. A stored type keeps its own: a document carrying
  none leaves them, and one carrying different rules, or leaving out a field a stored rule
  compares, is refused; `PUT /api/content-types/{name}/uniqueness` is the way to change them. An
  entry holding values a stored entry or an earlier entry of the document holds under a rule is
  refused by its index, unlike the singleton cap.
- Each entry goes through the same write path as `POST /api/contents`: a field the caller may not
  see is dropped, the entry is validated against its type as the document leaves it, the type's
  lifecycle hooks run, and the entry starts in the type's initial lifecycle state. The singleton cap
  is the one exception, since an apply of entries is a restore and lands what the document holds.
- A `token` field does not travel. An export leaves its value out of every entry, for every caller,
  and an apply gives each entry it creates a new token, discarding one a document carries. So
  moving a site through a document changes every token: move the database to keep tokens that are
  already printed or sent. A document that turns a stored field into a token, or a stored token
  into another type, is refused. See `docs/token-fields.md`.
- The apply is one database transaction, and each entry is written before the next is checked, so
  lifecycle hooks see the entries before it: journal entries are numbered in sequence, an account's
  parent and a page's parent from the same document resolve.
- Each record carries the `id` it had where it was exported, and its key in the document is that
  id. Apply gives it a new id, and a reference field holding the old id of another record in the
  document is pointed at the new one, whatever order the records are in. A reference to anything
  outside the document must exist where it is applied, so an entry referencing content left behind
  in another tenant is refused.
- A document carries entries, not files. A `file` field holds a stored file's id, and the apply
  checks it as a new attachment for the applying user, so an entry naming a file this tenant does
  not have is refused, and with it the whole apply. Restore the files first, with the same ids,
  then apply the document. See `docs/file-fields.md`.
- A stored type keeps its lifecycle when the document has none. A document that changes it, or
  adds one to a type that already has entries, is refused, and the refusal points at
  `PUT /api/content-types/{name}/lifecycle`, which edits a stored lifecycle.
- The entries' events are written together just before the commit, so a long apply leaves no
  gap in the event sequence and workflows run for every applied entry. The rows the apply writes
  are held for the whole transaction: run a large apply outside busy hours, since a post that
  needs the same row (the journal entry number sequence, a content type the document changes)
  waits for it and may need a retry.
- All or nothing. A refused type or entry is named by its key in the document (`'#2'` for the third
  record of an array, or the type name), and nothing is written, so the fixed document can be
  applied again without duplicating what would have landed. `POST /api/config/plan` answers 200
  and names the same refusals in its body, so a plan shows what the real apply would refuse.
- A document holds at most 5,000 entries (`Portability:MaxImportRecords`) and 500 content types.
  Past either, the apply is refused before anything is written. The export reads no more than that
  either, takes entries in id order, and says in its `gaps` when more are stored. Export fewer
  types with `?types=`, so each document stays under the limit, or raise the limit.
- The apply runs inside the calling tenant. A document carries no tenant identity of its own, which
  is what makes it safe to move between environments.

## Treat a document as sensitive

An export shows each entry the way the content read endpoints would show it to the caller who asked
for it. An entry the caller's read rule for its type does not allow is left out, the same entry
`GET /api/contents` leaves out, and so is an entry they may read nothing of; the export's `gaps`
says how many were left out. A field the caller may not read comes out under its mask and is listed
in the record's `maskedFields`.
Apply skips every field named in `maskedFields`, so a mask is never stored as a value. A full
backup therefore needs a caller who may read every entry and every field, such as SuperAdmin. A
role holding `export_content` and no read rule for a type exports none of that type's entries.

What the caller may read is still in the document, and it leaves the system's access control behind
the moment it is downloaded.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under [MIT](LICENSE).

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
