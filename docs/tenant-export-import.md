# Tenant export and import

One tenant out as a single JSON file, and a new tenant made from it. Part of #897.

## Export

`GET /api/tenants/{handle}/export`, for SuperAdmin or a role holding `manage_tenants`.

The file holds:

- `config`: what `GET /api/config/export` writes for that tenant (content types, roles, permission
  rules, workflows, connectors, site settings, forms), so there is one description of each section.
- `contents`: every entry except the `site` entry (that is `siteSettings`), written a page of 200 at
  a time straight to the response. The request does not hold the tenant in memory.
- `contentsWithheld`, `contentsRedacted` and `complete`: entries the caller may not read are left out
  and counted, an entry written with a field their read rules masked or removed is counted as redacted
  (the field is named in its `maskedFields`), and a config section the caller lacks the read
  capability for is named under `config.withheld`. `complete` is false when any of these happened.
- The export is recorded in the audit log before the body starts, so an aborted download still leaves
  a `tenant.exported` entry. Entries are read in id order with keyset paging.

The default tenant cannot be exported, because `default` is a reserved handle.

Not in the file: members and accounts (so no password hash), API keys, mail settings, stored files,
redirects, the audit log, domains, sign-up and sign-in rules. Connector secrets are named, never
written. Generated token fields are left out of entries.

## Import

`POST /api/tenants/import` with `{ "handle": "new-handle", "name": "optional", "bundle": { ... } }`.

- The handle must be new. A handle that exists answers 409 and nothing is written.
- The handle is claimed first as a Suspended tenant, which nothing serves, so two imports of one
  handle cannot both go on. The bundle is then applied through the config engine in one transaction,
  so the caller needs the capability each section asks for, as with `POST /api/config/apply`. Only
  after it committed is the tenant made Active, with the caller as its administrator.
- If the plan or the apply is refused or fails, the claim is removed, no tenant is returned and the
  plan that explains the refusal is.
- Entries need the Portability module (the `contents` section). Without it the bundle is refused.
- At most `Tenants:MaxImportEntries` entries (default 5000) per import.

## Still to do under #897

Move, archive and erase; members, API keys by reference, redirects and stored files in the bundle;
a chunked import for tenants over the entry limit; the shared walker over every tenant-keyed
document that erase needs.
