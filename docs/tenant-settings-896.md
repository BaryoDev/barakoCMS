# Tenant settings (#896)

Design call for #896, and what the first tranche ships.

## Decision

A tenant override sits next to the host default and wins field by field. The host default stays
exactly what it is today, so a deployment that sets no override changes nothing.

- The host default is the existing `EmailSettings` row (and host configuration under it).
- A tenant override is another `EmailSettings` row in the same table, with `TenantSlug` set and an id
  derived from the slug (`EmailSettings.IdForTenant`). The table stays single tenanted, like roles,
  so no table or index changes and no migration is needed. The existing row has no `TenantSlug` and
  is the host default.
- Resolution per field: tenant row, then host row, then configuration. A tenant that sets only a
  sender keeps the host's key.
- The key is encrypted with `ISecretProtector`, as for the host row, and no response has a field
  that could hold it. Reads say only whether a key is set.

## Endpoints

`GET` and `PUT /api/tenant-settings/email`, scoped to the request's tenant and gated on
`manage_settings` (held by a tenant's Admin through its membership). Null leaves a field alone, an
empty string clears it so the host default applies again. The default tenant has no override and
gets a 400 pointing at `/api/settings/email`. The existing `/api/settings/email` routes are
unchanged.

## Not in this tranche

- Module-declared sections, keys, categories and descriptions (#890), and a generic tenant
  settings document over them.
- Tenant overrides for Files.S3 and ExternalAuth.
- Sender and key overrides apply to mail sent inside a request for that tenant. Background work that
  runs with no tenant uses the host default.
