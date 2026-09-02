<p align="center">
  <img src="assets/logo.svg" alt="BarakoCMS logo, a coffee bean" width="120" height="120" />
</p>

<h1 align="center">BarakoCMS</h1>

<p align="center"><strong>A headless CMS suite for .NET 10: an event-sourced engine, opt-in modules, an admin UI, and a PWA kit.</strong></p>

<p align="center">
  <a href="https://www.nuget.org/packages/BarakoCMS"><img src="https://img.shields.io/nuget/v/BarakoCMS.svg" alt="NuGet" /></a>
  <a href="https://baryo.dev/docs"><img src="https://img.shields.io/badge/docs-baryo.dev-blue" alt="Documentation" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/BaryoDev/barakoCMS" alt="License" /></a>
  <a href="https://playground.baryo.dev/barakocms"><img src="https://img.shields.io/badge/demo-live-brightgreen" alt="Live demo" /></a>
</p>

BarakoCMS is a headless, API-first CMS built on [FastEndpoints](https://fast-endpoints.com/) and
[Marten](https://martendb.io/) (event sourcing over PostgreSQL). The core stays small and generic;
everything else, from accounting and analytics to email, file storage and auth providers, ships as **opt-in
modules** you compose per project. It comes with a Next.js **admin UI** that surfaces your content
*and* every installed module, and it's **multi-tenant** out of the box.

> The name **Barako** comes from *kapeng barako*, a bold Philippine coffee varietal, hence the
> coffee-bean mark. The full-module image is "Barako"; the lean core is "Decaf".

<p align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/screenshots/overview.png" alt="BarakoCMS admin, Overview" width="900" />
</p>

### What it is, and what it is not

**Reachable.** No sales call, no partner tier, no procurement cycle, no discovery call. Clone it and
it runs. That is the part deliberately not for sale.

**Free at any scale.** MPL-2.0, no seat cap, no revenue cap, no metered AI, and every module in this
repository included rather than sold separately. The [contributor terms](CLA.md) bound the licence
grant to OSI-approved licences, so this cannot be closed later even if someone wanted to.

**Not an enterprise vendor.** No SLA, no support contract, no SOC 2, no ISO 27001, no third-party
penetration test, no continuity guarantee. If your evaluation involves legal and a security
questionnaire, that is a real gap and you should weigh it.

What there is: [docs/compliance-posture.md](docs/compliance-posture.md) states what exists, what
does not, and which questions self-hosting moves to you rather than to us, which is most of them.
[SECURITY.md](SECURITY.md) carries the support and end-of-life policy. Beyond that you get the whole
system, the reasoning behind it in [DECISIONS.md](DECISIONS.md), and the ability to fix anything
yourself.

**Versioning.** Semantic within a major. Public members are not removed or resignatured inside a
major version; the old form is kept and marked obsolete with a removal version at least one major
away. See [CHANGELOG.md](CHANGELOG.md).

### What it does not do yet

Named here rather than discovered later. All of these are real:

| | |
| :--- | :--- |
| Multi-language content | No variants. One language per content item |
| Multi-site | Tenancy is not the same primitive |
| Approval workflow | "Workflow" here means automation rules, not draft, review and sign-off |
| Media library | File upload and download exist; cropping, focal points and variants do not |
| Forms builder | Absent |
| GraphQL | REST only |
| Managed hosting | Self-host or nothing |

If any of those is load-bearing for your project, [Umbraco](https://umbraco.com) is free too, MIT
licensed, and has all of them. That is a genuine recommendation, not a hedge.

---

## Contents

- [Quick start](#quick-start) · [Live demo](#live-demo) · [The admin](#the-admin) · [Modules](#modules)
- [Frontend kit](#frontend-kit) · [Architecture](#architecture)
- [How the pieces fit](#how-the-pieces-fit) · [Module, or core?](#module-or-core) · [Why this and not that](#why-this-and-not-that)
- [Docs](#documentation) · [Support](#support) · [License](#license)

---

## Quick start

The fastest path is the **[quickstart bundle](quickstart/)**, the full suite (core + every module),
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
[getting-started guide](https://baryo.dev/docs/).

---

## Live demo

**<https://playground.baryo.dev/barakocms>**. Sign in as `demo_admin` / `BarakoDemo2026!`. The API
is at `https://playground.baryo.dev/barakocms-api` ([health](https://playground.baryo.dev/barakocms-api/health)).

---

## The admin

A Next.js admin for modeling content, managing access, and running the system. Installed modules
appear automatically as their own sections, so the admin is a window into your whole deployment.

- **Content.** Define content types with typed fields (including per-field sensitivity/masking),
  write and version entries, and automate with workflows.
- **Access.** Users, roles, and groups with fine-grained RBAC.
- **Multi-tenancy.** Auto-scopes to your tenant on sign-in, with a switcher to move between the
  tenants you belong to; all data reloads under the one you pick.
- **Module sections.** Accounting, Analytics, Email events, Feature flags, PWA installs, and more,
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
project, through the same `IBarakoModule` contract you can implement yourself.

| Module | Package | What it adds |
| --- | --- | --- |
| **Accounting** | [`BarakoCMS.Accounting`](https://www.nuget.org/packages/BarakoCMS.Accounting) | A double-entry **ledger**: chart of accounts, balanced journal entries, balances, and per-account ledgers. |
| **Import** | [`BarakoCMS.Import`](https://www.nuget.org/packages/BarakoCMS.Import) | **Bulk import** `.xlsx`/CSV into content via [Talaan](https://github.com/BaryoDev/Talaan), through the CMS's own validation, permissions, and event sourcing. |
| **Files** | [`BarakoCMS.Files`](https://www.nuget.org/packages/BarakoCMS.Files) | **File upload/download** stored in Postgres via Marten: receipts, photos, documents. |
| **Email.Resend** | [`BarakoCMS.Email.Resend`](https://www.nuget.org/packages/BarakoCMS.Email.Resend) | An `IEmailService` over the [Resend](https://resend.com) API, plus a delivery webhook and an **email-events** feed (bounces/complaints). |
| **DeviceTrust** | [`BarakoCMS.DeviceTrust`](https://www.nuget.org/packages/BarakoCMS.DeviceTrust) | Remembers trusted devices; step-up OTP when a new one signs in. |
| **ExternalAuth** | [`BarakoCMS.ExternalAuth`](https://www.nuget.org/packages/BarakoCMS.ExternalAuth) | "Continue with Google / GitHub / Facebook / LinkedIn" via OAuth, behind one master switch. |
| **FeatureFlags** | [`BarakoCMS.FeatureFlags`](https://www.nuget.org/packages/BarakoCMS.FeatureFlags) | Create, toggle, and target flags by tenant, user, or percentage: viewable/toggleable in the admin. |
| **Portability** | [`BarakoCMS.Portability`](https://www.nuget.org/packages/BarakoCMS.Portability) | Export/import content-type definitions and data as a JSON bundle, for backup, migration, and seeding. |
| **Diagnostics** | [`BarakoCMS.Diagnostics`](https://www.nuget.org/packages/BarakoCMS.Diagnostics) | Captures client-side (browser) errors and shows a deduped, resolvable **error log** in the admin. |
| **Analytics.Umami** | [`BarakoCMS.Analytics.Umami`](https://www.nuget.org/packages/BarakoCMS.Analytics.Umami) | A server-side proxy over self-hosted [Umami](https://umami.is): visitors, pages, referrers, countries, devices, plus registering sites and verifying install. |
| **Pwa** | [`BarakoCMS.Pwa`](https://www.nuget.org/packages/BarakoCMS.Pwa) | Tracks PWA installs / installed-app launches (anonymous or tied to the signed-in user) so the admin shows **who** installed the app. |
| **AI** | [`BarakoCMS.AI`](https://www.nuget.org/packages/BarakoCMS.AI) | **Semantic search** over published content using a self-hosted embedding model ([Ollama](https://ollama.com) by default), with no third-party API key. Indexes only public fields; results are re-checked as published + public at query time. |

Enable the ones you want when you register the CMS:

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new BarakoCMS.Accounting.AccountingModule());
    modules.Add(new BarakoCMS.Email.Resend.ResendEmailModule());
    modules.Add(new BarakoCMS.Analytics.Umami.UmamiAnalyticsModule());
    modules.Add(new BarakoCMS.Pwa.PwaModule());
    modules.Add(new BarakoCMS.AI.AiModule()); // semantic search (Ollama)
    // …add only what you need
});

await app.RunBarakoModuleSeedersAsync(); // module baseline data (roles, reference data)
```

A module contributes DI services, its own Marten documents, FastEndpoints endpoints, and seed data,
implementing only the hooks it needs. See each module's page in the [docs](https://baryo.dev/docs/).
Want every module in one image? Use **`ghcr.io/baryodev/barako-cms`** (Barako, full suite); for the
lean core, **`ghcr.io/baryodev/barako-cms-decaf`** (Decaf) and add your own.

---

## Frontend kit

BarakoCMS is headless, so you build the frontend. These BaryoDev packages help:

- **[@baryodev/pwa-kit](https://github.com/BaryoDev/pwa-kit).** Service-worker registration + versioned
  caching, install hints, standalone viewport handling, and a PWA-install reporter that pairs with the
  `Pwa` module (`reportPwaStatus`).
- **[@baryodev/read-aloud](https://github.com/BaryoDev/read-aloud).** A "listen to this" control using Microsoft
  Edge's free neural voices: a Node TTS endpoint plus a framework-free browser reader with word
  highlighting. Drop it into any frontend for accessible, read-aloud content.
- **[Talaan](https://github.com/BaryoDev/Talaan).** A zero-dependency `.xlsx`/CSV reader used by the
  Import module.

---

## Architecture

- **Event-sourced.** Content changes are events in Marten; you get full version history, rollback,
  and async projections for free.
- **Modular.** Core knows nothing about any module; the `IBarakoModule` contract wires services,
  documents, endpoints, and seeders. Build your own the same way.
- **Multi-tenant.** Conjoined tenancy: one deployment, many tenants; data scoped by tenant, with
  global users/roles and per-tenant memberships.
- **RBAC.** Roles, groups, and per-content-type permissions, with field-level sensitivity/masking.
- **MFA.** Optional TOTP second factor (authenticator app) with one-time recovery codes; enforced on
  every sign-in path (password, email code, social), with encrypted secrets and replay protection.
- **Public delivery.** Anonymous, cacheable, published-only reads for content types explicitly
  opted in to public delivery, with
  **keyword search** (`/api/public/{type}/search`), an **RSS feed** (`/api/public/{type}/feed.xml`),
  and, via the AI module, **semantic search** (`/api/public/{type}/semantic`). It emits only
  allowlisted public fields, fail-closed by design.
- **Scheduled publishing.** Arm any item with a publish and/or unpublish time; a background sweep
  promotes and retires content on schedule, per tenant, emitting real events so workflows fire.
- **FastEndpoints + Kestrel.** Minimal-overhead HTTP; **health checks** and Prometheus **metrics**
  built in.

### How the pieces fit

```text
                      ┌──────────────────────────────────────────┐
   admin UI ─────────▶│  /api/contents  /api/content-types       │
   (Next.js)          │  /api/users  /api/roles  /api/workflows  │  authenticated
   barako-client ────▶│  /api/api-keys  /api/tenants  /api/audit │
                      └──────────────────────────────────────────┘
                                        │
   any browser ──────▶  /api/public/{type}  ──┐                     anonymous, and only
                        /feed.xml  /search    │                     for types opted in
                                        ┌─────▼──────┐
                                        │    CORE    │
                                        │            │
                                        │  content   │  a document bag + a runtime
                                        │  types     │  type definition, not classes
                                        │  auth      │  JWT, API keys, MFA, RBAC
                                        │  tenancy   │  conjoined: one database, many tenants
                                        │  delivery  │  opt-in, field-masked, fail-closed
                                        │  workflows │  events in, actions out
                                        └─────┬──────┘
                                              │ IBarakoModule
                    ┌─────────────────────────┼─────────────────────────┐
                    ▼                         ▼                         ▼
              Accounting                    Files ◀── Files.S3        AI
              FeatureFlags                  Import                    Analytics.Umami
              Portability                   Email.Resend              Pwa
              ExternalAuth                  DeviceTrust               Diagnostics
                    │                         │                         │
                    └─────────────────────────┼─────────────────────────┘
                                              ▼
                                       PostgreSQL (Marten)
                                  documents + event streams, one store
```

A module contributes services, its own document types, its own endpoints and its own seed data.
Core knows none of them by name. See [MODULES.md](MODULES.md) for the contract.

### Module, or core?

The question every contribution runs into, so here is the answer we use.

**It is a module if any of these is true:**

| Test | Because |
| --- | --- |
| Two sensible projects would disagree about wanting it | Core is what nobody gets a choice about, and every choice belongs to the person deploying |
| It names a vendor, a product or a business domain | `Resend`, `Umami`, `S3`, `Accounting`. Core's vocabulary is content, users, tenants, permissions and nothing else |
| It brings a dependency core does not already carry | An SDK, an API client, a file format. Core's dependency list is a promise to everyone who installs it |
| Removing it still leaves a working CMS | If content, auth, tenancy and delivery survive without it, it was never core |

These tests assume the module contract can express what you are building. Where it cannot, the
answer is core by necessity rather than by principle, and it should say so out loud: `Workflows`
below is exactly that case.

**It is core if any of these is true:**

| Test | Because |
| --- | --- |
| Something else cannot work without it | Content, content types, auth, tenancy, permissions. Everything stands on these |
| Core would need to know it exists | A module registers itself. The moment core needs an `if` for your feature, it is not a module |
| It changes the shape of content, auth, tenancy or delivery | These are contracts other people's modules depend on, so they are not extendable from outside |

When it is genuinely borderline, build it as a module. Moving a module into core later is additive.
Pulling a feature out of core is a breaking change for everyone who installed it.

#### Where core stands against its own rules

Rules are easier to trust when someone has applied them to their own work, so here is the result of
doing that.

**`Features/Club/` failed the naming test and has been removed.** It was per-club membership
management at `/api/club/*`, added during the multi-tenancy rollout and never called by the admin UI,
`barako-client`, or anything else in the repo. "Club" is product vocabulary rather than CMS
vocabulary, and core had no business carrying it.

Worth separating two things it was easy to conflate. The **endpoints** were product-shaped and are
gone. The **`Membership` model** they operated on is emphatically core: it is the join between a user
and a tenant carrying their roles and status there, and `TokenIssuer` reads it to decide which roles
go into a token, `PermissionResolver` reads it to answer every authorisation question, and
`CheckTenantAccessAsync` uses it to decide whether a token may be minted for a tenant at all. Multi
tenancy and authorisation both stop working without it.

The lesson is the one that generalises: **a feature failing the rules does not mean the model under
it is wrong.** Ask what the endpoints are called and what the data actually is, separately. If tenant
member management is wanted back, it belongs in `Features/Tenants/` under `/api/tenants/members`,
built deliberately rather than inherited from a product that needed it once.

**`Features/Workflows/` looks like a module and is correctly core.** It is the largest feature here,
plenty of deployments would not want it, and it names no vendor. By the first and fourth tests it
looks like an obvious module. It is core for a specific reason: it runs as a Marten **projection**,
registered in core's store setup, and `IModuleSchema` deliberately exposes only `For<T>()`. A module
can add document types; it cannot add a projection. So workflows could not be a module today even if
we wanted it to be. That is a real limit of the contract rather than a judgement about workflows, and
if a module ever needs a projection, that is the thing to change first.

**`Features/Audit/` looks optional and is correctly core.** One endpoint, and a CMS runs fine without
anyone reading the audit log. But fifteen core files across auth, content and permissions *write*
audit entries. The reading endpoint is the visible tip of something core produces continuously.
Making it a module would leave core writing records that nothing could read, which is worse than
either alternative.

**`Features/ApiKeys/`, `Me/`, `Settings/`, `Monitoring/` all stay.** They are thin, but each is part
of a contract other things stand on: API keys are an authentication method, `Me` is the self-service
half of auth, settings are read by core, and health and metrics are how a container is judged alive.

### Why this and not that

**Marten, not EF Core.** Content is a `Dictionary<string, object>` whose shape comes from a
`ContentTypeDefinition` created at runtime by a user. There are no compile-time entities for an ORM
to map, and migrations cannot describe a type somebody invents after deployment. Marten stores each
document as JSONB and queries into it, which is the same shape as the problem. With EF Core we would
be building a document store on top of an ORM built to avoid one.

**Marten, not a plain document store.** Version history and rollback are features, not extras.
`/api/contents/{id}/history` reads the event stream directly (`Events.FetchStreamAsync`), so history
is the source of truth rather than an audit table kept alongside and hoped to agree with it. Reading
a past version applies today's field-sensitivity rules on the way out, so a field made private since
is masked in history too rather than being readable by anyone who knows the version endpoint.

**PostgreSQL, not SQL Server.** Partly forced: Marten is built on Postgres and its event store uses
Postgres features, so choosing Marten chooses Postgres. Independently right though. JSONB with GIN
indexing is what makes a runtime-defined content bag queryable at all, and PostgreSQL can be
self-hosted on most platforms with no database licence fee, which matters for something people
deploy themselves.

**FastEndpoints, not Minimal API.** The module system is the reason.
`services.AddFastEndpoints(o => o.Assemblies = moduleAssemblies)` discovers endpoints inside module
DLLs, which is what lets a module ship its own routes without core knowing they exist. With Minimal
API every module's endpoints would have to be hand-registered by the host, and "install the package,
restart, done" stops being true. The rest is a bonus: route, roles and validator sit together in one
class, so an endpoint's authorisation is visible in the same file as its handler rather than in a
policy table somewhere else.

**FastEndpoints, not MVC controllers.** One class per operation, so a change to one endpoint touches
one file. Controllers accumulate: a `ContentController` with eight actions is eight reasons to open
the same file and eight chances to break something unrelated.

**No repository pattern.** `IDocumentSession` is injected directly into endpoints. A repository over
Marten would be a thin pass-through that hides the query capabilities we actually use, and the usual
argument for one, swapping the database, is not a swap we plan or could make cheaply.

**Conjoined tenancy, not database-per-tenant.** One database, tenant-scoped rows, filtered on a
`tenant_id` column by Marten and checked again against the tenant the caller's token names. A
database per tenant means migrations to run N times and N connection pools, and it is not
reversible: conjoined to separate is a data migration, and separate to conjoined is worse.

That filter is enforced by the application, not by Postgres. Row-level security is not implemented
(#446), so a bug that opens a session without a tenant has nothing underneath it, and the blast
radius of one is every tenant rather than one of them. `docs/multi-tenancy.md` sets out what is
actually enforced and what is not. If you need isolation a bug cannot cross, database-per-tenant is
the same Marten API and is the escape hatch.

**Public delivery opt-in, not opt-out.** It used to be opt-out, and modelling members or a ledger as
content produced an anonymous endpoint for them that nobody asked for. On a live deployment it did
exactly that. Publishing is a decision, so it is made explicitly, and field-level sensitivity still
applies on top.

**`AutoCreate.CreateOnly` in production, not `CreateOrUpdate`.** It creates missing objects so a
fresh database works, and never alters an existing one, so it cannot attempt a live migration that
is not safe. A single-to-conjoined event tenancy change is exactly such a migration, and it took down
content creation on a live instance once. Development keeps `CreateOrUpdate` for a fast local loop.

Deep dives live in the [docs](https://baryo.dev/docs/): event sourcing, concurrency,
content modeling, extending BarakoCMS, and deployment.

---

## Documentation

Full docs at **<https://baryo.dev/docs>**: getting started, guides, module references, API
reference, and architecture. Changelog: [CHANGELOG.md](CHANGELOG.md).

In this repo: [the public delivery API](docs/delivery-api.md) (pagination, the `filter[field][op]`
syntax, sorting, resolving references), [upgrading to 4.0](docs/upgrading-to-4.0.md),
[event-sourced content types](docs/event-sourced-content-types.md),
[backup and restore](docs/backup-and-restore.md), and
[compliance posture](docs/compliance-posture.md).

How this project is built and shipped: [AI Development Lifecycle](AI_DEVELOPMENT_LIFECYCLE.md),
the breakable-staging discipline, version-gated releases, and how tests gate every promotion.

---

## Support

BarakoCMS is free and open-source under [MPL-2.0](LICENSE). If it's useful to you:

- ⭐ **Star the repo** so others find it
- ☕ **[Ko-fi](https://ko-fi.com/T6T01CQT4R)** (one-time) or **[GitHub Sponsors](https://github.com/sponsors/BaryoDev)** (monthly)
- 🐛 **Contribute.** Issues, PRs, docs
- 📧 Commercial/enterprise support: [arnelirobles@gmail.com](mailto:arnelirobles@gmail.com)

---

---

## Contributors

People who have made barakoCMS better. Not only code: a bug report that saves a weekend, or a review
that stops a wrong issue being built, counts as much here as a pull request.

<!-- ALL-CONTRIBUTORS-LIST:START - Do not remove or modify this section -->
<table>
  <tbody>
    <tr>
      <td align="center" valign="top" width="14.28%">
        <a href="https://github.com/BabuBahir">
          <img src="https://avatars.githubusercontent.com/BabuBahir?s=90" width="90px;" alt="BabuBahir"/><br />
          <sub><b>BabuBahir</b></sub>
        </a><br />
        <a href="https://github.com/BaryoDev/barakoCMS/issues?q=author%3ABabuBahir" title="Bug reports">🐛</a>
        <a href="#review-BabuBahir" title="Reviewed issues">👀</a>
        <a href="#ideas-BabuBahir" title="Ideas and planning">🤔</a>
      </td>
      <td align="center" valign="top" width="14.28%">
        <a href="https://github.com/ahmdkaml">
          <img src="https://avatars.githubusercontent.com/ahmdkaml?s=90" width="90px;" alt="ahmdkaml"/><br />
          <sub><b>ahmdkaml</b></sub>
        </a><br />
        <a href="#ideas-ahmdkaml" title="Ideas and planning">🤔</a>
      </td>
    </tr>
  </tbody>
</table>
<!-- ALL-CONTRIBUTORS-LIST:END -->

Follows the [all-contributors](https://allcontributors.org) specification. To add someone, comment
`@all-contributors please add @username for bug, code` on any issue or pull request.

## License

[MPL-2.0](LICENSE) for the core and every module, so there is one licence across the suite.

MPL is file-level copyleft: use it in commercial and closed-source products freely, and if you
modify a barakoCMS source file, share that file's changes. Your own code stays yours.

*Packages up to `BarakoCMS` 3.1.1 were released under Apache-2.0 and remain so; 3.2.0 onward is MPL-2.0.*

**Author:** Arnel Robles · [@arnelirobles](https://github.com/arnelirobles) · [arnelirobles@gmail.com](mailto:arnelirobles@gmail.com)

---

Come say hello on [Discord](https://discord.gg/7GYKzDx7Z2) for questions, ideas, or just to tell us what you're building.

If barakoCMS is useful to you, a star helps other people find it. Contributions are welcome: code, documentation, module icons and artwork all count. See [CONTRIBUTING.md](CONTRIBUTING.md).
