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

## Module-declared sections

A module lists `SettingsSections` (section name, keys, category, description, secret flag, tenant or
host scope). `GET` and `PUT /api/tenant-settings/{section}` work for any declared section. An
unknown section answers 404 and an unknown key 400. Secret keys are write-only. Resolution is
tenant override, then the stored host default, then configuration.

Declared today: Email.Smtp (From), Files.S3 (Bucket, PublicBaseUrl, AccessKey, SecretKey) and AI
(EmbeddingModel, EmbeddingBaseUrl).

## Not in this tranche

- The Files.S3 and AI sections are declarations only. Their services still read host configuration.
- ExternalAuth has no tenant override.
- A tenant SMTP From applies to business mail sent for a tenant. Account mail keeps the host sender.
- Sender and key overrides apply to mail sent inside a request for that tenant. Background work that
  runs with no tenant uses the host default.
