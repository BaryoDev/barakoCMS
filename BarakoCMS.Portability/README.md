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

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new BarakoCMS.Portability.PortabilityModule());
});

var app = builder.Build();
app.UseBarakoCMS();
```


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
- The import runs inside the calling tenant. A bundle carries no tenant identity of its own, which
  is what makes it safe to move between environments.

## Treat a bundle as sensitive

An export contains whatever the content contains. If any of it is Sensitive, the bundle is too —
it leaves the system's access control behind the moment it is downloaded.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 8. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under MPL-2.0.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
