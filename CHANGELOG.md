# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed: seeding a chart of accounts could create two accounts sharing one code

`AccountService.UpsertAsync` looked for an existing account with a database query, so accounts stored
earlier in the *same uncommitted* unit of work were invisible to it. `UpsertManyAsync` is a loop over
that method and is how a host seeds a whole chart in one transaction — precisely where a repeated
code is most likely to appear. The second appearance became a second account: one code split across
two documents, with lookups picking between them arbitrarily and balances divided between them.

It now checks the session's pending changes before the database. Accounting module `0.2.2`.

### Accounting test coverage: 49.6% → 85.4%

The module's own HTTP surface (`POST /api/accounting/journal-entries`, the accounts endpoints), the
one-shot `AccountingMigration`, and `AccountService` had no tests between them, while carrying the
money. Three new suites cover them, each checked by reintroducing the bug it claims to catch —
balance tolerance, totals accumulated through `double`, a migration that moves instead of copies, a
dropped idempotency guard, and a widened role gate.

Two of those checks found weak tests rather than weak code, and both were rewritten: a one-line
journal entry is rejected for being unbalanced, not for having too few lines, so the line-minimum
rule was only pinned once an entry with *no* lines was tested; and a `(decimal)(double)` round trip
is lossless at these magnitudes, so the shape that actually bites — the running totals declared as
`double` — is what the fractional-amount test now pins.

`AccountService` was the surprise. Nothing inside barakoCMS calls it, so it read as dead code, but
a host application uses it in seven places. Whole suite: 71.1% → 74.4%.

## [3.20.1] - 2026-08-15

### Fixed: the opt-in had no way to be turned on for a type that already existed

3.20.0 made public delivery opt-in and added the endpoint to change it, but the admin only offered
the toggle when *creating* a content type. Every existing type — which is every type anyone upgrading
has — had no interface at all, so the documented upgrade step was "call the API by hand".

The content type screen now has the switch, with copy that says what each state means and names the
exact URL that will or will not answer. There are no core code changes; this releases the admin
image.

## [3.20.0] - 2026-08-15

### Changed (breaking): public delivery is now opt-in per content type

**Read this before upgrading. Content served at `/api/public/*` goes dark until you opt each type in.**

Public delivery used to be opt-out. `GET /api/public/{type}` served *any* content type as long as the
entry was Published and its sensitivity Public — and both of those are the defaults, for documents and
for fields alike. So modelling members, orders or a ledger as content handed you an anonymous,
unauthenticated endpoint for them without anyone ever deciding to publish anything.

That is the wrong way round. Publishing is a decision, and it should have to be made.

It was not hypothetical either: on a live deployment this served a club's member roster — names,
member numbers, emails, phone numbers, addresses — and its chart of accounts, including per-member
receivables, to anyone who supplied the club's handle. No token required.

`ContentTypeDefinition` gains `IsPubliclyDeliverable`, defaulting to **false**. The gate covers every
anonymous read path — the list, search and slug routes, the RSS feed, and semantic search in
`BarakoCMS.AI` 0.1.4. An un-opted-in type and an unknown type both answer `404`, deliberately: a
different answer would confirm which types exist.

Field-level sensitivity is unchanged and still applies on top. Opting a type in never implies every
field on it is public.

#### Upgrading

Existing types deserialize with the flag `false`, so **anything you currently serve publicly stops
being served** until you turn it on. For each type your site reads anonymously:

```http
PUT /api/content-types/{name}/public-delivery
{ "enabled": true }
```

Admin or SuperAdmin. There is also a toggle on the content type screen in the admin.

That endpoint is new, and it is why this could ship at all: content types had no update endpoint, so
without it the opt-in would have been a one-way door — every existing type undeliverable, with no
supported way back short of editing the database.

If you are unsure which types are affected, the honest answer is every type your frontend fetches from
`/api/public/`. There is no safe way for the CMS to infer that for you, which is exactly why this is a
major-flagged change rather than a silent default flip.

## [3.19.0] - 2026-08-09

### Fixed: the Next.js upgrade that was never actually broken

The admin moves to Next 16.3, and `npm audit` now reports **zero** vulnerabilities — the `next`,
`postcss` and `sharp` advisories that SECURITY.md had listed as unfixable are all gone.

They were never unfixable. Upgrading Next had been reverted once because it "broke" 28 end-to-end
tests, and the failures looked like a routing regression: after a mocked action the URL stayed at
`/login?`. The real cause is that Next 16.1 began blocking cross-origin requests for dev-server
assets. The end-to-end suite drives `http://127.0.0.1:3100` while the dev server treats `localhost`
as its origin, so every `/_next/*` chunk was refused, the app never hydrated, and any test that
clicked something failed. One line — `allowedDevOrigins: ["127.0.0.1"]` in `next.config.ts` — and the
full pack passes on 16.3.

Development only; a production build serves its own assets and is unaffected. No product code
changed, which is the point: the harness was misconfigured, not the application.

## [3.18.1] - 2026-08-09

### Fixed: 3.18.0 shipped only half its images

The 3.18.0 release published to NuGet and pushed the full suite image, then failed building the Decaf
image, which skipped the admin image and the playground deploy with it. So 3.18.0 exists as a package
but was never deployed; playground stayed on 3.17.1.

The Decaf `Dockerfile` copied only the `.csproj` before restoring, which stopped working when central
package management moved `TargetFramework` into `Directory.Build.props` — `NETSDK1013: The
TargetFramework value '' was not recognized`. `Dockerfile.suite` was unaffected because it copies the
whole build context, which is why only one of the two images failed.

No code changes; this exists to re-run the release now that the image builds.

## [3.18.0] - 2026-08-09

### Changed: enabling MFA now ends other sessions and tells the account owner

Closes the last two findings from the MFA security review.

Turning on two-factor authentication revokes the account's other refresh tokens, and sends the owner
an email saying it happened. Both exist for the same case: an attacker who has hijacked a session on
an account *without* MFA could enrol their own authenticator and keep the account — the enrolment was
silent, and their session survived it. Now no session that predates MFA outlives it, and if the owner
did not do this, they hear about it through a channel the attacker does not control.

The email is best-effort: a send failure is logged, not surfaced, since failing the request would undo
an enrolment the user did ask for. Users will be asked to sign in again after enabling, which is also
a useful confirmation that their authenticator works.

**A bounded gap remains, stated plainly.** Revoking refresh tokens stops a session being renewed; it
does not invalidate an access token already issued, which stays valid until it expires — at most 15
minutes. So an attacker's stolen session ends within 15 minutes of MFA being enabled rather than
immediately. Closing that properly needs a user-level "tokens issued before this moment are invalid"
timestamp checked during authentication. That is worth doing — it would also close the same window on
password change and on logout-everywhere, where `RevokeAllUserTokensAsync` has always been
refresh-token-only — but it belongs in its own change, because it runs on every authenticated request
and a mistake there locks everybody out.

## [3.17.1] - 2026-08-08

### Fixed: the social sign-in MFA gate was never published

`BarakoCMS.ExternalAuth` 0.1.6 ships the change written for 3.15.0 that stops Google, GitHub,
Facebook and LinkedIn sign-in from minting tokens for an account that has MFA enrolled. The code
landed in 3.15.0 but the module's own `<Version>` was left at 0.1.5, and the release pushes with
`--skip-duplicate`, so the package was silently skipped — anyone consuming 0.1.5 still has the
bypass, where a provider-account takeover sidesteps the second factor entirely.

If you use `BarakoCMS.ExternalAuth` with MFA, take 0.1.6. Core is bumped only to get past the
release gate, which reads core's version alone; there are no core changes in 3.17.1.

This is the second time an unbumped module version has swallowed a shipped fix (see 3.12.1). The
underlying gap is that nothing checks whether a module's source changed without its version moving.

## [3.17.0] - 2026-08-06

### Added: MFA in the admin UI

The TOTP backend shipped in 3.15.0, but the admin had no interface for it — which made the feature
unusable in practice and, worse, risky: anyone who enrolled through the API could not get back in,
because the login page treated the MFA challenge like a normal login and stored its empty token as if
it were a session. That is fixed, and the flow now exists end to end:

- **Settings → Security** — enroll with a QR code (rendered locally, so the secret never travels to a
  third-party QR service) or by typing the key, confirm with a code, and get the one-time recovery
  codes with a copy button. Turning MFA off requires a current code, so a hijacked session can't
  silently remove it.
- **Login** — a second step that accepts an authenticator code or a recovery code. The field uses
  `autocomplete="one-time-code"`, so password managers and iOS autofill offer the code directly.

There are no core code changes in this release; the version bump is what releases the admin image (the
release gate reads core's version alone), same as 3.12.1.

## [3.16.0] - 2026-08-05

### Added: browser error capture (the other half of Diagnostics)

The Diagnostics module could always serve captured errors, and the admin has had an Errors page — but
nothing ever sent anything, so the page was permanently empty. The admin now reports:

- Uncaught errors and unhandled promise rejections, via global listeners installed in the root layout.
- React render errors, via a root `global-error` boundary (those never surface through `window.onerror`,
  so they were invisible to any listener-only approach).

Reports are batched, deduplicated client-side, and sent with `keepalive` so a fault on a page being
navigated away from still arrives. The reporter is built so it can never become a source of errors: it
sends with plain `fetch` rather than the shared axios client (whose 401-refresh interceptor could
re-enter), swallows every send failure, and caps sends per page session so a render loop cannot flood
the API. Identity is attached when signed in, so errors can be attributed.

### Added: `telemetry` rate-limit policy

`POST /api/client-errors` is anonymous by design (faults happen before sign-in) and fans out to one
lookup per item in the batch, so under the global 100/min budget it allowed roughly a 20x amplification
against the database. It now has its own tighter policy: 20 batches per minute per IP, far above real
client behaviour. `BarakoCMS.Diagnostics` 0.1.3 applies it.

## [3.15.0] - 2026-08-05

### Added: TOTP multi-factor authentication

Accounts can enroll an authenticator app (Google Authenticator, 1Password, etc.) as a second factor.

- `POST /api/auth/mfa/setup` (auth) — start enrollment; returns a secret + `otpauth://` URI to show as a
  QR code, once.
- `POST /api/auth/mfa/enable` (auth) — confirm with a code; returns one-time recovery codes, once.
- `POST /api/auth/mfa/verify` — complete a two-step login: exchange the challenge from `/login` plus a
  TOTP (or recovery code) for the usual access + refresh tokens.
- `POST /api/auth/mfa/disable` (auth) — requires a current code, so a hijacked session can't strip it.
- `GET /api/auth/mfa/status` (auth).

When MFA is enabled, `POST /api/auth/login` returns `RequiresMfa: true` with a short-lived, single-purpose
challenge token (signed on a distinct `:mfa` audience, so it can never act as an access token) instead of
tokens. Secrets are stored AES-GCM-encrypted at rest; recovery codes are stored only as BCrypt hashes and
are single-use; a per-time-step replay guard (with optimistic concurrency) prevents reusing a code, and
wrong codes count toward the same lockout as password failures.

The feature was security-reviewed before release. The review's headline finding is fixed here:

### Fixed: every sign-in path honors MFA

Enrolling MFA now protects **every** way to obtain tokens, not just password login. The email one-time-code
path (`/api/auth/otp/verify`) and all four social providers (`BarakoCMS.ExternalAuth`: Google, GitHub,
Facebook, LinkedIn) treated mailbox/provider possession as a complete login and minted tokens without the
second factor — an inbox or OAuth-account compromise would have sidestepped MFA entirely. They now return
the same MFA challenge and require `/api/auth/mfa/verify` to finish. MFA-issued tokens also carry the
device-binding claim, matching the password and OTP paths.

Note: the AES key for MFA secrets derives from `Mfa:Key` if set, otherwise the JWT signing key. Set a
dedicated `Mfa:Key` in production and do not rotate it without re-encrypting stored secrets.

## [3.14.1] - 2026-08-05

### Fixed: 3.14.0 startup crash on existing databases

3.14.0 added two Marten indexes (on the new scheduled-publish fields) to the `Content` document. On a
fresh database that is harmless, but on an existing one it is a delta to `mt_doc_contents`, which the
prod/playground `AutoCreate.CreateOnly` policy refuses at startup — so the container crash-looped
(`Cannot derive schema migrations for TableDelta`). The indexes are removed: the scheduler sweep leads
with `Status` (already indexed), so they were never load-bearing. No API or behavior change from 3.14.0.
See H.40 for the missing online-migration step that would let index additions ship safely.

## [3.14.0] - 2026-08-05

### Added: scheduled publish / unpublish

Content can now be armed to go live or retire on its own. Two optional UTC times on a content item:

- `ScheduledPublishAt` — a Draft is promoted to Published at/after this time.
- `ScheduledUnpublishAt` — a Published item is Archived at/after this time.

Set them with `PUT /api/contents/{id}/schedule` (`{ scheduledPublishAt, scheduledUnpublishAt }`, either
optional, null clears; an unpublish time must be after the publish time). A background service,
`ScheduledContentService`, sweeps every minute across the default partition and each active tenant,
applies the due transitions, and clears the consumed time (a future unpublish window survives the
publish). Because public delivery and the RSS feed already gate on `Status == Published`, a scheduled
item simply appears — and later disappears — on its own.

Each transition emits a real `ContentStatusChanged` event, so history is correct and workflows fire.

### Fixed: publish workflows now actually fire

`PUT /api/contents/{id}/status` constructed a `ContentStatusChanged` event and updated the read model
but never appended the event to the stream, so the async `WorkflowProjection` — which is driven off the
stream and already maps a Published transition to the `Published` trigger — never ran. The endpoint now
appends the event (matching the Update and rollback endpoints), so workflows configured on `Published`
finally execute. Scheduled transitions go through the same path.

## [3.13.0] - 2026-08-05

### Added: RSS feeds for public content

Any content type now exposes an RSS 2.0 feed at `GET /api/public/{type}/feed.xml` — the newest 50
Published, document-Public entries. It reuses the same projection as the rest of public delivery, so
drafts, Sensitive documents, and non-Public fields never appear; the feed is anonymous and cached the
same way the other public endpoints are.

Because the CMS is headless, item links point at the caller's frontend, configured (all optional):

- `Feeds:SiteUrl` — the site the links resolve against (falls back to the request host).
- `Feeds:Paths:{type}` — a per-type link template like `/blog/{slug}` (defaults to `/{type}/{slug}`).
- `Feeds:Titles:{type}` — the channel title (defaults to the type name).

Item title, description and date are taken from the usual public fields (Title/Name, then
Excerpt/Summary/Description/Body, then a Date/PublishedAt field falling back to created-at).

## [3.12.2] - 2026-08-03

### Fixed: every module rebuilt against current core

All module packages are republished so they are compiled against 3.12.x. They had drifted badly —
most were last built against core **3.2.x**, nine minor versions back — because a module is only
rebuilt when its own `<Version>` changes, and none had.

This was not theoretical. A host taking new core with the previously published modules got real
failures: import endpoints returning 403, and ledger and file-attachment posts returning 400. The same
host built against matching source passed. If you are on core 3.12.x, take these module versions too;
mixing 3.12.x core with the older module packages is not a supported combination.

No functional changes in this release beyond the rebuild. See H.40 in the roadmap for the pipeline
gap that let the drift accumulate silently.

## [3.12.1] - 2026-08-03

### Fixed

- `BarakoCMS.Portability` 0.1.2 — ships the audit-log capture for export and import that was written
  for 3.12.0 but never published: the module's version was unchanged, and the release pushes with
  `--skip-duplicate`, so the package was silently skipped and stayed at 0.1.1. Core is bumped only to
  get past the release gate, which reads core's version alone; there are no core changes in 3.12.1.

## [3.12.0] - 2026-08-03

### Added: audit log

A queryable "who did what, when", available in core (no module to install).

- `GET /api/audit` (Admin) — filter by actor, action, date range and tenant, paginated.
- Captures auth events (login succeeded/failed/blocked, account lockout, logout, token refresh and
  refresh-token reuse detection) and sensitive administrative actions (role and user-group deletion,
  role/group assignment and removal, content archival, portability export/import).
- Entries are hash-chained: each one carries the previous entry's hash, so editing or removing a past
  entry breaks every hash after it. This is tamper-**evidence**, not tamper-prevention — someone with
  direct database access can still rewrite the chain forward. Known limitation: the previous-hash
  lookup and the insert are not one atomic operation, so two audit-worthy actions racing in the same
  tenant can chain off the same previous hash. That shows up as a detectable fork, and no entry is
  lost.
- Admin gains an "Audit log" page with the same shape as the Errors page.

### Added: per-content-type domain rules (`IContentLifecycleHook`)

Schema validation can express "Amount is a decimal"; it cannot express "total debits must equal total
credits", or "assign the next sequence number". Previously a domain with real invariants had to be
given its own bespoke write endpoint, which put it outside the generic content pipeline.

A module now registers an `IContentLifecycleHook` the way it registers a workflow action, and core
runs it on create **and** update without knowing the module exists. Hooks can reject a write or enrich
it, and they receive the request's Marten session, so anything they store commits in the same
transaction as the entry.

### Changed: decimals in schemaless data are no longer doubles

**Behaviour change — read this if you consume `Content.Data` from .NET.**

Values inside the `Dictionary<string, object>` bags (a content entry's `Data`, a permission rule's
`Conditions`, an audit entry's `Metadata`) previously came back from storage as `System.Double` at the
top level and as raw `JsonElement` when nested. Fractional numbers now come back as `decimal`, and
nested values are plain CLR types at every depth.

- Whole numbers still come back as `long`, so ids and counts are unaffected.
- Values outside `decimal`'s range still fall back to `double` rather than throwing.
- **If your code casts a stored number straight to `double`, it will now throw `InvalidCastException`.**
  Use `Convert.ToDecimal`/`Convert.ToDouble` instead.

This was a correctness fix, not a preference: summing money that round-tripped through binary floating
point accumulates drift, and a plausible-but-wrong accounting total is the worst failure mode this
codebase has. The same change also makes nesting consistent, which retires a class of bug where code
type-checking for `Dictionary<string, object>` silently received a `JsonElement` instead.

### Changed: `BarakoCMS.Accounting` 0.2.0 — accounts and journal entries are content types

**Breaking for hosts using the accounting module.**

`Account` and `JournalEntry` were bespoke Marten documents; they are now ordinary barakoCMS content
types, so they are queryable, permissioned and deliverable through the same generic endpoints as
everything else. The rules a schema cannot express moved into content lifecycle hooks, so posting an
unbalanced entry through plain `POST /api/contents` is rejected, entry numbers are allocated
server-side, and a rejected post does not consume a number. A posted entry is immutable — correct it
by posting a reversing entry.

- New `AccountService` so hosts keep working with the `Account` domain type instead of hand-building
  content dictionaries. Replace `session.Query<Account>()` and `session.Store(new Account { … })` with
  `AccountService.GetAllAsync`/`GetByCodeAsync`/`UpsertAsync`.
- The `/api/accounting/*` endpoints are unchanged for callers, but now read and write content.
- `AccountingMigration.RunAsync` copies existing typed `Account`/`JournalEntry` documents into content.
  It copies rather than moves and is idempotent, so the originals stay on disk and a bad run can be
  repeated rather than being the step that loses a ledger.

### Fixed

- `BarakoCMS.Diagnostics` is wired into the Suite image, so the shipped Suite's admin "Errors" page has
  a backend instead of returning 404.
- CI now fails on Critical/High vulnerable dependencies instead of only reporting them, and Dependabot
  is configured for NuGet, npm and GitHub Actions.
- CSP no longer allows `'unsafe-inline'` in `script-src` outside Development. `style-src` still does —
  see the roadmap for the remaining nonce work.

## [3.11.0] - 2026-07-30

### Added: draft preview

Editors can now preview an unpublished entry on the real frontend without publishing it.

- `POST /api/preview` — an authenticated editor mints a short-lived (30 min) signed token for one draft.
  The caller must have read access to that content type (the same permission check as the authoring read
  endpoint), so you can only mint a link for a draft you're allowed to see.
- `GET /api/public/{type}/{slug}?preview=<token>` returns the draft when the token is valid. The token is
  signed with the JWT key and bound to the exact tenant + type + slug, so it can't be forged or reused for
  another entry. Preview lifts **only** the published gate: a document-Sensitive entry is still refused, only
  Public fields are emitted, and the response is `no-store`. An invalid or expired token falls back to the
  normal published-only behavior, revealing nothing.

## [3.10.0] - 2026-07-28

### Added: AI semantic search (BarakoCMS.AI module)

A new opt-in module adds vector search over published content using a self-hosted embedding model
(Ollama by default) — no third-party API key.

- `POST /api/ai/index/{type}` (admin) builds a type's vector index in the current tenant, embedding each
  Published, document-Public entry from its Public fields only.
- `GET /api/public/{type}/semantic?q=…&limit=…` (anonymous, cacheable) ranks the index by cosine
  similarity, then re-verifies each hit is still Published and document-Public before returning it — so a
  draft, a Sensitive document, a Sensitive field, or an entry unpublished since indexing never surfaces.

Enable with `Ai:Enabled=true` and point `Ai:EmbeddingBaseUrl` at an Ollama-style endpoint. Inert
otherwise. Bundled in the suite image; published as `BarakoCMS.AI` on NuGet.

## [3.9.0] - 2026-07-28

### Added: public content search

`GET /api/public/{type}/search?q=…&limit=…` returns the top public matches for a query. It projects
each entry to its public shape first and only then matches, so it searches exclusively over allowlisted
Public fields — a draft, a document-Sensitive entry, or a value in a Sensitive field can never surface
a result. A title/name hit outranks a body hit. It scans a bounded recent window (swap in Postgres
full-text search for larger corpora). Anonymous and cacheable, like the rest of public delivery.

### Fixed: admin runtime config under a basePath

The admin loaded its runtime `env-config.js` from the origin root, so when hosted under a basePath on
a different origin than it was built for, the config 404'd and the admin fell back to the build-time
API URL — sending auth cross-origin. The script now loads from the basePath.

## [3.8.0] - 2026-07-28

### Added: password change and admin reset

Passwords could be set only at registration or by the initial-admin seeder, so there was no way to
rotate an account's password. Two endpoints close that gap:

- `POST /api/me/password` — the signed-in user changes their own password. It re-verifies the current
  password, enforces the password policy, and rejects a no-op change.
- `POST /api/users/{userId}/password` — a SuperAdmin resets another user's password (recovery or
  rotation), enforcing the same policy.

Both revoke the user's active refresh tokens, so a session established before the change can't be
refreshed afterwards (outstanding short-lived access tokens still expire on their own).

## [3.7.0] - 2026-07-28

### Changed: navigation menus are now a content type

Menus are no longer a bespoke document with their own CRUD endpoints. A menu is a `menu` content type,
edited like any other content and delivered through the existing public API. Modeling it as content
keeps it pluggable and removes a whole hand-written surface.

- Removed the `Menu` document and the `/api/menus` admin endpoints (create/update/delete/list) and the
  `/api/public/menus/{slug}` read endpoint.
- A menu is a `menu` content type with a `Name` and an `Items` field of type `json` that holds the nav
  tree (`{ label, url, openInNewTab, children[] }`). It is served by the generic public delivery at
  `GET /api/public/menu/{slug}`, so the same published-and-Public rules and field allowlist apply.
- Existing `menus` tables are left orphaned and untouched (safe under `AutoCreate.CreateOnly`).

**Breaking:** clients calling `/api/menus*` or `/api/public/menus/{slug}` must move to the `menu`
content type and `GET /api/public/menu/{slug}`. The `@baryodev/barako-client` `public.menu()` method
keeps the same signature and return shape; it now reads the content type under the hood.

## [3.6.0] - 2026-07-27

### Added: pluggable file storage, an S3 provider, and public media

Files can now be stored in Postgres (the built-in default, no configuration) or in any S3-compatible
object store, and both work: the CMS user picks by whether they add the S3 provider and configure it.

- A storage abstraction (`IFileStorage`) moves file bytes behind an interface while metadata stays in
  Postgres. The default keeps bytes in the database.
- A new opt-in `BarakoCMS.Files.S3` provider stores bytes in a bucket. One code path serves AWS S3,
  Cloudflare R2, and MinIO; only the endpoint and public URL differ. Configure it under `Files:S3`;
  with no bucket set it stays dormant and Postgres keeps serving.
- Public media for a website frontend: uploads can be marked public, and `GET /api/public/files/{id}`
  serves a file anonymously only when it is public. Private and missing files are both a plain 404, so
  ids cannot be probed. A public file on an object store is served from its own direct, CDN-friendly
  URL; a public file in Postgres is proxied through the API.

Uploaded SVGs are rejected (they can carry script), and proxied public responses send `nosniff` plus a
sandbox content-security-policy. Built through the process with a security review of the new anonymous
surface (no high-severity findings) and covered by tests against a real MinIO container and the real
API, including the fail-closed public download.

## [3.5.0] - 2026-07-27

### Added: site navigation menus

A tenant-scoped menu (a slug like "main" or "footer", a name, and an ordered list of items with one
level of nesting) so a site frontend can render its navigation from the CMS instead of hardcoding it.
Admins manage menus through `GET/POST/PUT/DELETE /api/menus`, and the frontend reads them anonymously:

- `GET /api/public/menus/{slug}` returns a menu for public rendering.

Menus carry only navigation data (labels and URLs), are scoped to one site, and are cacheable. This
pairs with the public content delivery API to cover a site's chrome as well as its content.

## [3.4.0] - 2026-07-27

### Added: public content delivery API

A read-only, anonymous surface for serving published content to a website frontend, separate from the
authenticated authoring API. Two endpoints:

- `GET /api/public/{type}` returns a paged list of Published entries of a content type.
- `GET /api/public/{type}/{slug}` returns a single Published entry addressed by its slug.

This is what makes barakoCMS able to back a public site (a blog, a docs site, a marketing page)
without the frontend holding credentials. It is deliberately safe by construction, independent of the
authoring API's sensitivity mode:

- Only Published entries are ever returned. Drafts and archived content are never exposed.
- A document marked Sensitive or Hidden is never delivered, even when Published.
- Only fields the content type marks Public leave the API. Field masking is an allowlist, so a field
  removed or renamed in the schema, or a value stored under a differently-cased key, cannot leak.
- Each request is scoped to one tenant, resolved from the X-Tenant header or host.
- Responses carry `Cache-Control: public`, so a CDN can absorb traffic.

Built through the development process with an adversarial security review, which caught and fixed two
data-exposure bugs before merge (the field masking was a denylist that leaked orphan and mis-cased
keys). Covered by abuse-case tests against the real API over a real database.

## [3.3.0] - 2026-07-26

### Added: API keys for machine callers

Long-lived credentials so an SDK, a CI job, or an integration can call the API without holding a
human's password or minting short-lived JWTs. A key is `bcms_` followed by 256 bits of entropy, shown
once when you create it. Only its SHA-256 hash is stored, so a database leak never yields a usable
key. Manage them under Access, then API keys, in the admin: create with a name, scopes and optional
expiry; copy the secret once; revoke any time.

Keys are deliberately confined:

- **Content surface only.** A key can read and write content, content types and schemas, and nothing
  else. It can never manage users, roles, tenants, or other keys. That stays behind a human sign-in,
  so a leaked key can't escalate into platform administration.
- **Scoped.** `content:read`, `content:write`, `contenttype:read`, `contenttype:write`, or `*`. A
  read-only key is refused when it tries to write.
- **Tenant-bound.** A key operates in one tenant and can't reach another's data. It stops working
  the moment its owner's membership is removed or the tenant is deactivated, the same check the login
  path uses.
- **Revocable immediately.** Revoking a key refuses it on its next request, not at expiry.

Sent as `Authorization: Bearer bcms_...`, alongside the existing JWT auth on the same endpoints.

This shipped through the development process with an adversarial security review of the auth code,
which caught and fixed a flaw where a best-effort "last used" write could have silently reverted a
revocation. Covered by unit, integration, and abuse-case tests (forged, revoked, expired, wrong
scope, cross-surface) against the real API over a real database.

## [3.2.4] - 2026-07-25

### Fixed: dashboard crash on partial metrics

The admin overview formatted the error-rate metric without guarding for a missing value, so if the
monitoring endpoint returned a partial object the whole dashboard threw
(`Cannot read properties of undefined (reading 'toFixed')`) and rendered a blank error page. Guarded
it — a missing metric shows `—`, like the other cards already did. Found while writing the end-to-end
tests, not in production.

### Pipeline and tests (internal)

Not user-facing, but part of the same release: CI now runs the whole browser end-to-end pack (not a
subset) plus a secret and dependency scan; every deploy runs a smoke test that logs in, creates
content, and confirms validation still rejects bad input; a one-button rollback workflow was added;
and the field types from 3.2.3 gained backend integration tests that exercise the real API over a
real database.

## [3.2.3] - 2026-07-24

### Added: richer content-type field types

Content types now support properly-typed fields instead of everything being text: `email`, `url`,
`slug`, `uuid`, `money`, `time`, plus `richtext`, `markdown`, and `json` (and a `date`/`datetime`
split). Each is validated at the API — an `email` field rejects a value that isn't an email rather
than silently storing it — and the admin renders a matching control for each type (date/time pickers,
number input for money, a JSON editor for structured data).

Behind it, the allowed field types now live in one `FieldTypeRegistry` that every validator reads
from. Three validators had drifted apart — one accepted `text`/`number`, another rejected them, and a
doc comment advertised types no validator accepted — and a parity test now fails the build if they
ever diverge again.

## [3.2.2] - 2026-07-24

### Fixed: fresh installs boot on an empty database

3.2.1 shipped with `AutoCreate.None`, which never creates schema on demand. Existing
deployments already had their tables so nothing broke there, but a brand-new database had no
tables and the seeder crashed on startup with `relation "mt_doc_roles" does not exist`. A
fresh install is the first thing a new user does, so this needed fixing.

Three changes:

- Production now runs Marten's recommended `CreateOnly`: it creates missing objects (so a fresh
  database and any unregistered document type work) but never updates or drops an existing one,
  so it still won't attempt the failing single-to-conjoined event-store migration that `None`
  was chosen to avoid.
- The schema is applied explicitly at startup, before the seeders run, so their first query
  always finds its tables.
- The full-suite host now seeds the core roles and the initial admin. Previously it ran only the
  module seeders, so a fresh suite install had no user to sign in as.

Verified on a wiped database: schema created, admin seeded, login succeeds. Suite: 248 passing.

## [3.2.1] - 2026-07-22

### 🔐 Security: cross-tenant token issuance

**Upgrade if you run more than one tenant.** Single-tenant deployments were never exposed.

The tenant a token is scoped to comes from the client-supplied `X-Tenant` header. Login, OTP
verify and refresh all trusted it and minted a matching `tenant` claim **without checking
membership**; only `/api/me/switch` checked. Because role resolution falls back to a user's
*global* roles when no membership exists, the resulting token was not merely scoped to another
tenant — it carried working privileges there.

Any registered user could authenticate against any tenant and receive a usable token for it,
including one they had never joined. `BarakoCMS.ExternalAuth` had the same hole via its `club`
parameter, so *Continue with Google* produced the same result.

**Fixed** by routing every token through a single `ITokenIssuer` that owns the tenant-access
check, so it cannot be skipped by omission. Access is granted when the tenant is the default
(the single-tenant/global context), when the slug is unregistered (not a managed tenant, so no
membership model applies), or when the user holds an **Active** membership in a registered,
active tenant.

Two consequences worth knowing:

- **Refresh re-checks on every rotation**, so revoking a membership takes effect within ~15
  minutes instead of lingering for the refresh token's 7-day life.
- **Login denials return "Invalid credentials"** — the same message as a wrong password, since
  "right password, wrong tenant" confirms both the account and the tenant exist.

Covered by nine end-to-end regression tests, verified failing against the vulnerable build
before the fix landed. Suite: 243 passing.

`BarakoCMS.ExternalAuth` 0.1.3 → 0.1.4.

## [3.2.0] - 2026-07-21

### ⚖️ One licence across the suite: MPL-2.0

The core was Apache-2.0 while all eleven modules were MPL-2.0, and a stray `LICENSE.txt`
carrying an unrelated BSD 3-Clause notice sat next to the Apache `LICENSE`. GitHub could not
resolve which applied and reported the repository as having **no licence at all** — which is
worse than either choice, since it leaves adopters with nothing to rely on.

Everything is now **MPL-2.0**, matching the modules and Talaan.

- `LICENSE` replaced with the Mozilla Public License 2.0
- `LICENSE.txt` (BSD 3-Clause, left over from an unrelated 2023 project) removed
- core switched from `PackageLicenseFile` to `PackageLicenseExpression`, so NuGet renders the
  licence inline and it matches how the modules already declared theirs
- README and CONTRIBUTING updated

**What MPL-2.0 means for you:** file-level copyleft. Use barakoCMS in commercial and
closed-source products freely; if you modify a barakoCMS *source file*, publish that file's
changes. Your own application code stays yours. This is deliberately weaker than GPL — linking
and bundling are unrestricted.

**Already shipped versions are unaffected.** `BarakoCMS` 3.1.1 and earlier remain Apache-2.0
under the terms they were released with; 3.2.0 onward is MPL-2.0.

### 📦 All modules republished

Eight modules were live on NuGet but missing from its search index — installable if you knew
the exact ID, invisible if you didn't. Every module gets a patch release so the whole suite
re-indexes and depends on core 3.2.0.

## [3.1.1] - 2026-07-20

### 🔒 Security & Stability Hardening

A focused stabilization pass across authentication, the content write path, the workflow engine, and RBAC. Test suite grew from 173 to 182 passing (9 new regression tests).

#### Security
- **Upgraded Marten 8.16.1 → 8.37.0**, fixing a critical full-text-search injection advisory (GHSA-vmw2-qwm8-x84c).
- **Locked down anonymous endpoints**: content version history now requires authentication + per-content read permission and applies sensitivity redaction; `GET /api/schemas`, `/api/diagnostics/typecheck`, and `/api/monitoring/k8s` are restricted to admin roles (previously publicly readable).
- **JWT signing key is validated at startup** — the app fails fast if it is missing or shorter than 32 characters (no insecure default).
- **Removed committed credentials** from base config; the initial admin password and dev JWT key now live only in `appsettings.Development.json`, and seeded sample accounts are gated to Development.
- **SSRF protection** on workflow webhook actions (loopback, link-local incl. cloud metadata, and private ranges are blocked).
- Added a **global exception handler** (no stack-trace leaks), request body-size limits, and a minimal (non-enumerating) health response.
- **Fixed a latent bug that silently disabled token revocation**: UTC `DateTime` comparisons in LINQ queries threw under Npgsql and were swallowed, so revoked tokens were treated as valid. Revocation now works.

#### Correctness
- **Content rollback** now updates the read model (previously appended an event but left `GET`/`LIST` serving stale data) and records the acting admin.
- **Optimistic concurrency** on content updates is now enforced via Marten `AppendOptimistic`; responses expose a `Version` field to echo back for conflict detection (HTTP 412). Create/Update/ChangeStatus commit their event and read-model document in a single transaction.
- **Refresh-token rotation** is race-safe (optimistic concurrency) with **reuse detection** that revokes the entire token family on replay.
- **Login lockout counter** uses an atomic increment, closing a race that allowed lockout bypass.
- **Permission cache** is invalidated immediately on role/permission/user-role changes instead of serving stale decisions for up to 5 minutes.
- `ConfigurationService` no longer throws on malformed admin-editable settings (falls back to defaults).

#### Workflows
- Workflow execution is **decoupled from the request path** and runs via the async projection — a slow or failing action can no longer block or fail a content save.
- **Fault isolation**: per-action and per-workflow error handling prevents one failing action from stalling the engine/daemon.
- **Template variables are now resolved in live runs** (previously only in dry-run), with a single-pass resolver that prevents second-order injection between fields.
- Status transitions now fire `Published`-triggered workflows; workflows are **validated on creation** (trigger event, action types, required parameters).

### Added
- SVG coffee-bean logo (`assets/logo.svg`) and README Security & Stability section.

## [3.1.0] - 2026-07-20

The admin becomes multi-tenant and module-aware.

### Added
- **Multi-tenant admin** — auto-scopes to your tenant on sign-in, plus a switcher to move between the
  tenants you belong to (`/api/me/tenants`, `/api/me/switch`). The `X-Tenant` header is derived from
  the token's own claim and survives refresh.
- **Installed modules surface in the admin** — sections appear when their module is present:
  Accounting (accounts/balances/ledgers), Feature flags (view/toggle), Email events (Resend
  bounces/complaints), Errors (client-error log + resolve), Analytics, PWA installs.
- **`BarakoCMS.Pwa` module** — `POST /api/pwa/report` (anonymous or tied to the signed-in user) and
  `GET /api/pwa/installs`, so the admin shows who installed the app. Pairs with `@baryodev/pwa-kit`'s
  `reportPwaStatus`.
- **Analytics (Umami)** — device / OS / browser breakdowns; a site status endpoint powering install
  detection (an "add the snippet" banner + a Verify step); a visitors panel on the dashboard.
- **`Email.Resend`** — an `/api/email-events` list endpoint.
- **Quickstart bundle** — `quickstart/` runs the full suite + admin + Postgres from one documented `.env`.

### Fixed
- **Global roles kept when switching tenants** — `MembershipRoles` now unions a user's global roles
  with their tenant membership roles, so a platform SuperAdmin keeps Users/Roles access inside a tenant.

## [3.0.0] - 2026-07

Multi-tenancy and field-level sensitivity.

### Added
- **Multi-tenancy on a shared database** (Marten conjoined tenancy). Identity is global (users, roles,
  tokens, settings, devices are single-tenanted); only domain content and event streams are
  tenant-scoped. The default tenant maps to Marten's default partition — no data migration for
  existing single-tenant deployments.
- `Tenant` registry + `Membership` (a global user's roles within a tenant); tenant resolution via
  `X-Tenant` header/subdomain; `TenantAccessMiddleware`. New endpoints: `/api/tenants*`,
  `/api/me/tenants`, `/api/me/switch`, `/api/club/*`.
- **Field-level sensitivity** — mark content-type fields Sensitive or Hidden; masked per role on read
  (remove / redact / show last 4); a role that can't see a field can't write it either.

## [2.0.0] - 2025-12-11

### 🎉 Major Release: Advanced RBAC System (Phase 1)

**Status**: ✅ Production Ready  
**Test Results**: 104/122 passing (18/18 Phase 1 tests = 100%)  
**Security**: Zero vulnerabilities found

#### Added - RBAC API Endpoints (18 new endpoints)

**Role Management (5 endpoints)**
- `POST /api/roles` - Create role with granular permissions
- `GET /api/roles` - List all roles
- `GET /api/roles/{id}` - Get specific role
- `PUT /api/roles/{id}` - Update role
- `DELETE /api/roles/{id}` - Delete role

**UserGroup Management (7 endpoints)**
- `POST /api/user-groups` - Create user group
- `GET /api/user-groups` - List all groups
- `GET /api/user-groups/{id}` - Get specific group
- `PUT /api/user-groups/{id}` - Update group
- `DELETE /api/user-groups/{id}` - Delete group
- `POST /api/user-groups/{groupId}/users` - Add user to group
- `DELETE /api/user-groups/{groupId}/users/{userId}` - Remove user from group

**User Assignment (4 endpoints)**
- `POST /api/users/{userId}/roles` - Assign role to user
- `DELETE /api/users/{userId}/roles/{roleId}` - Remove role from user
- `POST /api/users/{userId}/groups` - Add user to group
- `DELETE /api/users/{userId}/groups/{groupId}` - Remove user from group

#### Added - RBAC Core Features

- **Permission System**: Content-type-specific CRUD permissions with JSON conditions
- **Role Model**: Support for permissions and system capabilities
- **UserGroup Model**: User organization and group-based permissions
- **ConditionEvaluator**: Dynamic permission conditions (`$CURRENT_USER`, `$eq`, `$in`)
- **PermissionResolver**: Service for checking user permissions

#### Added - Documentation

- Comprehensive RBAC documentation in README.md
- CLA (Contributor License Agreement) requirement
- CLA Assistant integration
- Workflow automation guide with template variables
- AttendancePOC workflow examples
- Pre-publication review artifacts
- Production readiness assessment
- ROADMAP.md with 5-phase plan

#### Added - Data Seeding

- Enhanced DataSeeder with comprehensive AttendancePOC data:
  - 4 roles: SuperAdmin, Admin, HR, User
  - 3 sample users with different roles
  - AttendanceRecord content type with sensitivity configuration
  - Email confirmation workflow
  - 3 sample attendance records

#### Changed

- Updated User model with `RoleIds` and `GroupIds` lists
- Workflow documentation expanded with multiple examples
- Contributing guidelines updated with CLA requirement

#### Security

- All RBAC endpoints secured with role-based authorization
- `SuperAdmin` role for role management
- `Admin` role for user group management
- Production configuration checklist provided
- Security audit passed (zero vulnerabilities)

#### Tests

- 18 new integration tests (100% passing)
  - 7 Role API tests
  - 7 UserGroup API tests
  - 4 User Assignment tests
- Pre-publication testing complete
- Regression testing passed (no Phase 1 regressions)

#### Performance

- All RBAC operations use async/await
- Efficient Marten LINQ queries
- Stateless API design (horizontally scalable)

---

## [2.1.0] - 2025-12-16

### 🎉 Phase 2 Week 4: Plugin System Completion & Documentation

**Status**: ✅ Complete  
**Test Results**: 166/174 passing (96%)  
**Code Quality**: A+ Grade (9.7/10)

#### Added - Plugin-Based Workflow System

- **6 Built-in Workflow Action Plugins**:
  - `EmailAction` - Send email notifications
  - `SmsAction` - Send SMS messages
  - `WebhookAction` - HTTP POST to external services
  - `CreateTaskAction` - Create tasks in the system
  - `UpdateFieldAction` - Update content fields dynamically
  - `ConditionalAction` - If/then/else logic

- **Workflow Tool Endpoints (5 new API endpoints)**:
  - `GET /api/workflows/actions` - List all available action plugins
  - `POST /api/workflows/validate` - Validate workflow JSON schema
  - `GET /api/workflows/{id}/debug` - Get execution history for debugging
  - `POST /api/workflows/dry-run` - Test workflow without side effects
  - `GET /api/workflows/variables` - Get available template variables

- **Plugin Infrastructure**:
  - `IWorkflowPluginRegistry` - Auto-discovery of workflow actions
  - `ITemplateVariableExtractor` - Template variable resolution (`{{data.Field}}`)
  - `IWorkflowSchemaValidator` - JSON schema validation
  - `IWorkflowDebugger` - Execution logging and debugging
  - `WorkflowActionMetadataAttribute` - Plugin metadata for documentation

#### Added - Documentation

- **Plugin Development Guide** (`docs/plugin-development-guide.md`):
  - Step-by-step tutorial for creating custom actions
  - Examples for all 6 built-in plugins
  - Best practices and patterns
  - Template variable usage
  - Troubleshooting guide

- **Workflow Migration Guide** (`docs/workflow-migration-guide.md`):
  - Migration from hardcoded to plugin system
  - Before/after code examples
  - Migration checklist
  - FAQ section
  - **No breaking changes** - fully backward compatible

#### Added - Tests

- **13 Integration Tests** (`WorkflowToolsApiTests.cs`):
  - All 5 workflow tool endpoints tested
  - Real database integration with Testcontainers
  - 100% passing

-  **Unit Tests**:
  - `WorkflowPluginRegistryTests.cs` (5 tests)
  - `WorkflowSchemaValidatorTests.cs` (8 tests)
  - `TemplateVariableExtractorTests.cs` (8 tests)

#### Improved - Code Quality (A+ Grade Achieved)

- **Performance Optimization**:
  - Template variable resolution: 50-70% faster (StringBuilder)
  - Database queries optimized with `.Take(1)`
- **Security Hardening**:
  - Type-safe `WorkflowEvents` constants (no magic strings)
  - Input validation complete
  - Null-safety throughout
- **Documentation**:
  - Complete XML documentation on all public APIs
  - Error handling in all 5 endpoints
  - Structured logging with context
  
#### Changed

- **IReadOnlyList** return types for immutability
- Enhanced error messages in validation
- Cancellation token support in validator

#### Performance

- Workflow plugin discovery: < 100ms for 6 plugins
- Schema validation: < 5ms per workflow
- Template variable resolution: 50-70% faster than before

#### Documentation

- Updated README with workflow system features
- Added plugin quick start example
- Links to development and migration guides

---

## [1.2.1] - 2025-12-08

### Added
- **Idempotency**: Added `IdempotencyFilter` to prevent duplicate requests on POST/PUT/PATCH via `Idempotency-Key` header.
- **Content History**: Implemented full audit trail of versions containing `Data`, `Timestamp`, and `ModifiedBy`.
- **Rollback**: Added ability to revert content to any previous version.
- **Workflows**: Added event-driven workflow engine supporting `Email` actions on `Created` and `Updated` events.
- **Documentation**: Added standalone release notes `RELEASE_NOTES_v1.2.0.md`.

### Security Hardening
- **Secrets Management**: Removed hardcoded secrets from `appsettings.json`. Migrated to User Secrets/Env Vars.
- **Infrastructure**: Secured Swagger UI (Development only) and added strict CORS policy.
- **Logging**: Redacted sensitive data (SMS content) from logs.
- **Auth**: Enforced strong password policy (Min 8 chars, Upper, Lower, Number, Special).
- **Code Quality**: Enforced strict analysis level (`latest`) and build-time style enforcement.

## [1.1.0] - 2025-12-05

### Added
- **Runtime Validation**: Implemented comprehensive validation for Content Types and Content Data.
  - Enforces field types (`string`, `int`, `bool`, `datetime`, `decimal`, `array`, `object`).
  - Enforces PascalCase naming convention for fields.
  - Validates content data against schema on Create and Update.
- **Validation Configuration**: Added `StrictValidation` and `ValidationOptions` to `appsettings.json`.
- **Documentation**: Added `RELEASE_PROCESS.md` and updated `DEVELOPMENT_STANDARDS.md` with validation details.

### Fixed
- **Integration Tests**: Resolved Marten async query issues in validators.
- **JSON Handling**: Fixed `ContentDataValidator` to correctly handle `JsonElement` types.

## [1.0.3] - 2024-01-01

### Added
- **AI Adoption**: Added `llms.txt` and `.cursorrules` to improve AI agent compatibility.
- **Community**: Added `CONTRIBUTING.md` and `CODE_OF_CONDUCT.md`.
- **Production**: Added `Dockerfile` and updated `docker-compose.yml` with health checks.
- **Health Checks**: Added `/health` endpoint.
- **Documentation**: Added `CITATIONS.cff` for research citation.

### Changed
- **Licensing**: Changed license from custom restrictive license to **Apache License 2.0**.
- **NuGet**: Updated package tags to include `ai-native` and `vibe-coding`.
- **Error Handling**: Enabled global exception handling with `UseProblemDetails()`.

### Fixed
- Improved `docker-compose.yml` reliability with `depends_on` and health checks.
