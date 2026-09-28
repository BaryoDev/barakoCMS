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
