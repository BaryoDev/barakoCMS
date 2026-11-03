# API keys: service principals, capability scopes and rotation (#906)

## Decision

Build it in three steps. Each keeps today's behaviour as the default.

1. Capability scopes. A scope names a capability, and the scope processor maps more routes to scopes
   instead of denying them. Done so far: `files:read` and `files:write` for `/api/files`, and `monitor:read` for
   `GET /api/contents/counts`, `GET /api/analytics/**` and `GET /api/monitoring/health`.
2. Rotation with an overlap window. Done: `POST /api/api-keys/{id}/rotate`.
3. A service principal per tenant that owns keys and holds its own roles. Done, see below.

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

## Service principals

A `ServicePrincipal` is a global document with a tenant, a name and role ids. It has no login. A key
made with `servicePrincipalId` takes its roles from the principal, so it keeps working when the person
who made it loses membership. A key without one acts as its creator, as before.

All routes need the `manage_api_keys` capability:

- `POST /api/service-principals`, `GET /api/service-principals`, `GET /api/service-principals/{id}`
- `PUT /api/service-principals/{id}/roles`
- `POST /api/service-principals/{id}/disable`
- `POST /api/api-keys/move-to-principal`, opt in, with `{ servicePrincipalId, keyId }` or
  `{ servicePrincipalId, allManageable: true }`. Exactly one of the two. It writes an
  `apikey.moved_to_principal` audit row. A key the caller does not own is skipped, except for a SuperAdmin.

The authentication handler refuses a key (one generic 401) when its principal is missing, disabled or in
another tenant. It skips the creator membership check but still applies the tenancy mode and the
tenant-active check.

Claims: a principal has no `UserId`, so nothing treats it as a person and nothing reads a missing id.
`Username` is `principal:<name>`. `principal_id` and one `principal_capability` per capability of its
roles are added. `CallerCapabilities` ignores platform capabilities and `*` for a principal.
Endpoints that need a real user (they read the `UserId` claim) answer 401 or 400 for a principal key.
Principal keys are for content routes and for routes gated on a capability the key names with a `cap:`
scope.

Escalation guard, the same rule core uses when a person assigns roles to a membership: the SuperAdmin
role is never assignable, and another tenant's role answers "does not exist". The principal rule is
stricter. Platform roles and roles carrying a platform capability are refused for everyone, SuperAdmin
included. Roles are checked again on every request, so a hand-edited document grants nothing extra.

## Capability scopes

`cap:<capability>` lets a key reach a route gated on that capability, outside the content API. The
role gate still checks the roles the key acts with. `*` does not imply `cap:`. Names are checked against
the capability vocabulary at creation. A deny list in `CapScopes` refuses the capabilities that manage
keys, roles, tenants, users, membership, email settings and hidden fields, plus every platform capability,
because a leaked key must not be able to mint more access. It is checked at creation and on every request.

## Read-only keys

`readOnly: true` on `POST /api/api-keys` makes a `bcms_ro_` key. It refuses every POST, PUT, PATCH and
DELETE with 403 ("This API key is read-only.") whatever its scopes. It reads only entries,
`GET /api/workflows/health`, `GET /api/workflow-runs`, `GET /api/modules`, `GET /api/analytics/**` and
`GET /api/overview`, each still needing its usual scope. Rotating one yields another `bcms_ro_` key.

## Remaining

- Scopes for roles, workflows, connectors, module endpoints, the pages tree and export, through `cap:`
  where a capability gate exists.

## Usage

`GET /api/api-keys/{id}/usage` (tenant admin) returns when the key was last used, its refusals in the last 24 hours by reason (scope, content type, expired, revoked) and by content type, and the content types it names that no longer exist. The refusals are counted in memory, so each replica has its own and a restart clears them. A refused request for a type the key does not name and the tenant does not have is counted as `other`. `barakocms_api_key_refusals_total` counts the same refusals by reason only, since `/metrics` is anonymous.
