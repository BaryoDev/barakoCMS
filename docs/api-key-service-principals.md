# API keys: service principals, capability scopes and rotation (#906)

## Decision

Build it in three steps. Each keeps today's behaviour as the default.

1. Capability scopes. A scope names a capability, and the scope processor maps more routes to scopes
   instead of denying them. Done so far: `files:read` and `files:write` for `/api/files`, and `monitor:read` for
   `GET /api/contents/counts`, `GET /api/analytics/**` and `GET /api/monitoring/health`.
2. Rotation with an overlap window. Done: `POST /api/api-keys/{id}/rotate`.
3. A service principal per tenant that owns keys and holds its own roles. Not built yet.

## Why this order

Scopes and rotation add routes and a state change without touching stored keys. Existing keys keep
their scopes and keep resolving roles from the user who made them. A `*` key now also reaches
`/api/files`, still limited by its owner's roles and capabilities, so it can do nothing its owner
could not.

Rotation reuses the key's own expiry. The old key is not revoked, it expires at the end of the window,
which the authentication handler already enforces on every request. No new field and no migration.

The service principal changes stored keys and the authentication handler (which loads `key.UserId`
and that user's roles). It needs a migration that moves existing keys per tenant, so it is a separate
change.

## Remaining

- A service principal document, owned by a tenant, with its own roles and no login.
- Keys owned by a principal keep working when the admin who created them loses membership.
- A migration to move existing keys to a principal, per tenant, opt in.
- Scopes for roles, workflows, connectors, module endpoints, the pages tree and export. The files
  scopes are the pattern: one scope per capability, mapped in `ApiKeyScopeProcessor`.
