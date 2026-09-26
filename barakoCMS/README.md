<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS logo" />
  <h1>BarakoCMS</h1>
  <p><em>An open-source headless CMS for .NET 10.</em></p>
</div>

---

The core package: content types and content, authentication, role-based access control, workflow,
multi-tenancy, and a public delivery API. Event-sourced on [Marten](https://martendb.io) over
PostgreSQL.

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
app.Run();
```

That is a working CMS. Add modules for anything else.

## What you get

- **Content types defined at runtime** — no rebuild to add a field
- **RBAC** with per-field sensitivity, so a field can be hidden without hiding the record
- **Workflow** with pluggable actions
- **Multi-tenancy**, conjoined on one database
- **Public delivery API** for reading published content
- **Event sourcing**, so content has real history rather than a last-writer-wins row
- **Blueprints** that create a set of content types in one call, including the `site` settings a
  renderer reads its identity and theme from
- **Collection syncs** that fill a content type from a request or a feed on a schedule, and
  **collection push** (`POST /api/collections/{type}/push`) for a source that sends its entries

## Modules

Optional packages, each installable on its own:

| Package | Adds |
|---|---|
| `BarakoCMS.Accounting` | Double-entry ledger |
| `BarakoCMS.AI` | Semantic search, self-hosted embeddings |
| `BarakoCMS.Analytics.Umami` | Privacy-friendly analytics |
| `BarakoCMS.DeviceTrust` | Device binding and approval |
| `BarakoCMS.Diagnostics` | Browser error capture |
| `BarakoCMS.Email.Resend` | Transactional email |
| `BarakoCMS.Email.Smtp` | Transactional email over any SMTP relay |
| `BarakoCMS.ExternalAuth` | Google / GitHub / Facebook / LinkedIn sign-in |
| `BarakoCMS.FeatureFlags` | Flags with targeting |
| `BarakoCMS.Files` + `.S3` | Uploads stored in Postgres, or in S3-compatible storage with `.S3` |
| `BarakoCMS.Forms` | Public form submissions into a content type |
| `BarakoCMS.Import` | Bulk import |
| `BarakoCMS.Pages` | Page tree, navigation and path resolution over a content type |
| `BarakoCMS.Portability` | Export / import bundles |
| `BarakoCMS.Pwa` | Install tracking |

Every one is published under the `barakocms-module` tag, so one search on nuget.org returns them all.

For module authors:

| Package | Adds |
|---|---|
| `BarakoCMS.Abstractions` | The module contract, models, events and service interfaces, without the core |
| `BarakoCMS.Testing` | A test host: the core over a Testcontainers PostgreSQL with your module registered |
| `BarakoCMS.Templates` | `dotnet new barakocms-module`, a module and its test project |

## Documentation

Full documentation and deployment guides live in the
[repository](https://github.com/BaryoDev/barakoCMS). The console is
[barakoBrew](https://github.com/BaryoDev/barakoBrew).

## Part of barakoCMS

This is the **core package**. Modules are separate, optional packages that build on it, each
published under the `barakocms-module` tag so a single search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under MPL-2.0.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
