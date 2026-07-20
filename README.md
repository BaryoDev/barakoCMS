<p align="center">
  <img src="assets/logo.svg" alt="BarakoCMS logo — a coffee bean" width="120" height="120" />
</p>

<h1 align="center">BarakoCMS</h1>

<p align="center"><strong>A headless CMS suite for .NET 8: an event-sourced engine, opt-in modules, an admin UI, and a PWA kit.</strong></p>

<p align="center">
  <a href="https://www.nuget.org/packages/BarakoCMS"><img src="https://img.shields.io/nuget/v/BarakoCMS.svg" alt="NuGet" /></a>
  <a href="https://baryo.dev/barakoCMS"><img src="https://img.shields.io/badge/docs-baryo.dev-blue" alt="Documentation" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/BaryoDev/barakoCMS" alt="License" /></a>
  <a href="https://playground.baryo.dev/barakocms"><img src="https://img.shields.io/badge/demo-live-brightgreen" alt="Live demo" /></a>
</p>

BarakoCMS is a headless, API-first CMS built on [FastEndpoints](https://fast-endpoints.com/) and
[Marten](https://martendb.io/) (event sourcing over PostgreSQL). The core stays small and generic;
everything else — accounting, analytics, email, file storage, auth providers — ships as **opt-in
modules** you compose per project. It comes with a Next.js **admin UI** that surfaces your content
*and* every installed module, and it's **multi-tenant** out of the box.

> The name **Barako** comes from *kapeng barako*, a bold Philippine coffee varietal — hence the
> coffee-bean mark. The full-module image is "Barako"; the lean core is "Decaf".

<p align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/screenshots/overview.png" alt="BarakoCMS admin — Overview" width="900" />
</p>

> [!NOTE]
> BarakoCMS is a passion project built for learning and portfolio purposes. It's usable and
> maintained, but **breaking changes can happen** between versions. See [CHANGELOG.md](CHANGELOG.md).

---

## Contents

- [Quick start](#quick-start) · [Live demo](#live-demo) · [The admin](#the-admin) · [Modules](#modules)
- [Frontend kit](#frontend-kit) · [Architecture](#architecture) · [Sample app](#sample-app)
- [Docs](#documentation) · [Support](#support) · [License](#license)

---

## Quick start

The fastest path is the **[quickstart bundle](quickstart/)** — the full suite (core + every module),
the admin UI, and PostgreSQL, from prebuilt images, driven by one documented `.env`. No build, no
clone.

```bash
curl -O https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/quickstart/docker-compose.yml
curl -O https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/quickstart/.env.example
cp .env.example .env      # then set DB_PASSWORD, JWT_KEY (32+ chars), ADMIN_PASSWORD
docker compose up -d
```

- **Admin UI** → <http://localhost:3000> · **API** → <http://localhost:5005> · health at `/health`
- Every module ships in the image and stays off/mock until you add its keys, so you grow into Umami
  analytics, Resend email, social sign-in, and the rest without touching the compose.

See **[quickstart/README.md](quickstart/README.md)** for every variable, enabling modules, and going
behind a domain with TLS. To build from source instead, see the
[getting-started guide](https://baryo.dev/barako-cms/guide/getting-started).

---

## Live demo

**<https://playground.baryo.dev/barakocms>** — sign in as `demo_admin` / `BarakoDemo2026!`. The API
is at `https://playground.baryo.dev/barakocms-api` ([health](https://playground.baryo.dev/barakocms-api/health)).

---

## The admin

A Next.js admin for modeling content, managing access, and running the system. Installed modules
appear automatically as their own sections — the admin is a window into your whole deployment.

- **Content** — define content types with typed fields (including per-field sensitivity/masking),
  write and version entries, and automate with workflows.
- **Access** — users, roles, and groups with fine-grained RBAC.
- **Multi-tenancy** — auto-scopes to your tenant on sign-in, with a switcher to move between the
  tenants you belong to; all data reloads under the one you pick.
- **Module sections** — Accounting, Analytics, Email events, Feature flags, PWA installs, and more,
  each shown only when its module is installed.

<table>
  <tr>
    <td><img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/screenshots/analytics.png" alt="Analytics" /></td>
    <td><img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/screenshots/content-types.png" alt="Content types" /></td>
  </tr>
  <tr>
    <td><img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/screenshots/pwa-installs.png" alt="PWA installs" /></td>
    <td><img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/screenshots/health.png" alt="Health" /></td>
  </tr>
</table>

---

## Modules

Core stays lean and generic. Capabilities ship as **optional NuGet modules** you opt into per
project — the same `IBarakoModule` contract you can implement yourself.

| Module | Package | What it adds |
| --- | --- | --- |
| **Accounting** | [`BarakoCMS.Accounting`](https://www.nuget.org/packages/BarakoCMS.Accounting) | A double-entry **ledger** — chart of accounts, balanced journal entries, balances, and per-account ledgers. |
| **Import** | [`BarakoCMS.Import`](https://www.nuget.org/packages/BarakoCMS.Import) | **Bulk import** `.xlsx`/CSV into content via [Talaan](https://github.com/BaryoDev/Talaan), through the CMS's own validation, permissions, and event sourcing. |
| **Files** | [`BarakoCMS.Files`](https://www.nuget.org/packages/BarakoCMS.Files) | **File upload/download** stored in Postgres via Marten — receipts, photos, documents. |
| **Email.Resend** | [`BarakoCMS.Email.Resend`](https://www.nuget.org/packages/BarakoCMS.Email.Resend) | An `IEmailService` over the [Resend](https://resend.com) API, plus a delivery webhook and an **email-events** feed (bounces/complaints). |
| **DeviceTrust** | [`BarakoCMS.DeviceTrust`](https://www.nuget.org/packages/BarakoCMS.DeviceTrust) | Remembers trusted devices; step-up OTP when a new one signs in. |
| **ExternalAuth** | [`BarakoCMS.ExternalAuth`](https://www.nuget.org/packages/BarakoCMS.ExternalAuth) | "Continue with Google / GitHub / Facebook / LinkedIn" via OAuth, behind one master switch. |
| **FeatureFlags** | [`BarakoCMS.FeatureFlags`](https://www.nuget.org/packages/BarakoCMS.FeatureFlags) | Create, toggle, and target flags by tenant, user, or percentage — viewable/toggleable in the admin. |
| **Portability** | [`BarakoCMS.Portability`](https://www.nuget.org/packages/BarakoCMS.Portability) | Export/import content-type definitions and data as a JSON bundle, for backup, migration, and seeding. |
| **Diagnostics** | [`BarakoCMS.Diagnostics`](https://www.nuget.org/packages/BarakoCMS.Diagnostics) | Captures client-side (browser) errors and shows a deduped, resolvable **error log** in the admin. |
| **Analytics.Umami** | [`BarakoCMS.Analytics.Umami`](https://www.nuget.org/packages/BarakoCMS.Analytics.Umami) | A server-side proxy over self-hosted [Umami](https://umami.is): visitors, pages, referrers, countries, devices — plus registering sites and verifying install. |
| **Pwa** | [`BarakoCMS.Pwa`](https://www.nuget.org/packages/BarakoCMS.Pwa) | Tracks PWA installs / installed-app launches (anonymous or tied to the signed-in user) so the admin shows **who** installed the app. |

Enable the ones you want when you register the CMS:

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new BarakoCMS.Accounting.AccountingModule());
    modules.Add(new BarakoCMS.Email.Resend.ResendEmailModule());
    modules.Add(new BarakoCMS.Analytics.Umami.UmamiAnalyticsModule());
    modules.Add(new BarakoCMS.Pwa.PwaModule());
    // …add only what you need
});

await app.RunBarakoModuleSeedersAsync(); // module baseline data (roles, reference data)
```

A module contributes DI services, its own Marten documents, FastEndpoints endpoints, and seed data,
implementing only the hooks it needs. See each module's page in the [docs](https://baryo.dev/barako-cms/).
Want every module in one image? Use **`ghcr.io/baryodev/barako-cms`** (Barako, full suite); for the
lean core, **`ghcr.io/baryodev/barako-cms-decaf`** (Decaf) and add your own.

---

## Frontend kit

BarakoCMS is headless — you build the frontend. These BaryoDev packages help:

- **[@baryodev/pwa-kit](https://github.com/BaryoDev/pwa-kit)** — service-worker registration + versioned
  caching, install hints, standalone viewport handling, and a PWA-install reporter that pairs with the
  `Pwa` module (`reportPwaStatus`).
- **[@baryodev/read-aloud](https://github.com/BaryoDev/read-aloud)** — "listen to this" using Microsoft
  Edge's free neural voices: a Node TTS endpoint plus a framework-free browser reader with word
  highlighting. Drop it into any frontend for accessible, read-aloud content.
- **[Talaan](https://github.com/BaryoDev/Talaan)** — a zero-dependency `.xlsx`/CSV reader used by the
  Import module.

---

## Architecture

- **Event-sourced** — content changes are events in Marten; you get full version history, rollback,
  and async projections for free.
- **Modular** — core knows nothing about any module; the `IBarakoModule` contract wires services,
  documents, endpoints, and seeders. Build your own the same way.
- **Multi-tenant** — conjoined tenancy: one deployment, many tenants; data scoped by tenant, with
  global users/roles and per-tenant memberships.
- **RBAC** — roles, groups, and per-content-type permissions, with field-level sensitivity/masking.
- **FastEndpoints + Kestrel** — minimal-overhead HTTP; **health checks** and Prometheus **metrics**
  built in.

Deep dives live in the [docs](https://baryo.dev/barako-cms/): event sourcing, concurrency,
content modeling, extending BarakoCMS, and deployment.

---

## Sample app

**[BaryoClub](https://github.com/BaryoDev/BaryoClub)** composes the suite into a club membership and
treasury manager — members, a double-entry ledger, statements, OTP sign-in, and a full PWA. It's the
reference for building a real product on BarakoCMS.

---

## Documentation

Full docs: **<https://baryo.dev/barakoCMS>** — getting started, guides, module references, API
reference, and architecture. Changelog: [CHANGELOG.md](CHANGELOG.md).

---

## Support

BarakoCMS is free and open-source under [Apache-2.0](LICENSE). If it's useful to you:

- ⭐ **Star the repo** so others find it
- ☕ **[Ko-fi](https://ko-fi.com/T6T01CQT4R)** (one-time) or **[GitHub Sponsors](https://github.com/sponsors/BaryoDev)** (monthly)
- 🐛 **Contribute** — issues, PRs, docs
- 📧 Commercial/enterprise support: [arnelirobles@gmail.com](mailto:arnelirobles@gmail.com)

---

## License

[Apache-2.0](LICENSE). Modules are published under MPL-2.0. Attribution required.

**Author:** Arnel Robles · [@arnelirobles](https://github.com/arnelirobles) · [arnelirobles@gmail.com](mailto:arnelirobles@gmail.com)
