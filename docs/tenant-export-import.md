# Tenant export and import

One tenant out as a single JSON file, and a new tenant made from it. Part of #897.

## Export

`GET /api/tenants/{handle}/export`, for SuperAdmin or a role holding `manage_tenants`.

The file holds:

- `config`: what `GET /api/config/export` writes for that tenant (content types, roles, permission
  rules, workflows, connectors, site settings, forms), so there is one description of each section.
- `contents`: every entry except the `site` entry (that is `siteSettings`), written a page of 200 at
  a time straight to the response. The request does not hold the tenant in memory.
- `contentsWithheld` and `complete`: entries the caller may not read are left out and counted, and a
  config section the caller lacks the read capability for is named under `config.withheld`.
  `complete` is false when either happened.

Not in the file: members and accounts (so no password hash), API keys, mail settings, stored files,
redirects, the audit log, domains, sign-up and sign-in rules. Connector secrets are named, never
written. Generated token fields are left out of entries.

## Import

`POST /api/tenants/import` with `{ "handle": "new-handle", "name": "optional", "bundle": { ... } }`.

- The handle must be new. A handle that exists answers 409 and nothing is written.
- The tenant is created Active with the caller as its administrator, then the bundle is applied
  through the config engine in one transaction, so the caller needs the capability each section asks
  for, as with `POST /api/config/apply`.
- If the apply is refused the tenant is removed again and the plan that explains the refusal is
  returned.
- Entries need the Portability module (the `contents` section). Without it the bundle is refused.
- At most `Tenants:MaxImportEntries` entries (default 5000) per import.

## Still to do under #897

Move, archive and erase; members, API keys by reference, redirects and stored files in the bundle;
a chunked import for tenants over the entry limit; the shared walker over every tenant-keyed
document that erase needs.
