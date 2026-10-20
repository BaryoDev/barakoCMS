# Accounts

An account owns tenants. It is for an agency or reseller that runs tenants for others and wants
its staff to manage those tenants and no one else's.

## The default account

Every tenant belongs to an account. A tenant with no `accountId` belongs to the implicit default
account (id `00000000-0000-0000-0000-0000000a0001`, slug `default`). It has no stored document until
you set its branding, so a deployment that never creates an account behaves as it did before.

## Who manages what

- A caller holding `manage_tenants` (or SuperAdmin) manages every tenant and every account, as before.
- A user made a manager of an account lists and edits only that account's tenants through
  `GET /api/tenants`, `POST /api/tenants` and `PUT /api/tenants/{handle}`. A tenant of another
  account answers 404 on update. Moving a tenant between accounts needs `manage_tenants`.
- On create, a manager of exactly one account creates into it. A manager of several names `accountId`.

Grants are read on every request, so revoking one takes effect without a new token.

## Routes

| Route | Who |
| --- | --- |
| `GET /api/accounts` | `manage_tenants`: every account, default first. A manager: their own accounts. |
| `GET /api/accounts/{slug}` | the same, 404 for an account the caller does not manage |
| `POST /api/accounts`, `PUT /api/accounts/{slug}` | `manage_tenants` only |
| `GET`, `POST /api/accounts/{slug}/managers`, `DELETE /api/accounts/{slug}/managers/{userId}` | `manage_tenants` only |
| `GET /api/accounts/branding` | any signed-in caller: the branding of the account that owns their current tenant |

## Console branding

An account carries `name`, `logoUrl` and `colours` (a map such as `primary` to `#112233`).
`GET /api/accounts/branding` returns them for the console. With no colours set on the account it
answers the tenant's own `Branding` map, and with no name or logo it answers null so the console
keeps its own.

## Content types owned by an account

An account can own a content type. Every tenant in the account uses it: it appears in
`GET /api/content-types` with `ownerAccountId` set, and an entry written in any of those tenants is
validated against it. The entries themselves stay in the tenant that wrote them. A tenant outside
the account does not see the type, and an entry it writes under that name is not validated by it.

| Route | Who |
| --- | --- |
| `GET /api/accounts/{slug}/content-types` | `manage_tenants`, or a manager of the account |
| `POST /api/accounts/{slug}/content-types` | the same: `name`, `displayName`, `description`, `fields` |
| `POST /api/accounts/{slug}/content-types/{name}/fields` | the same, with the body of `POST /api/content-types/{name}/fields` |
| `DELETE /api/accounts/{slug}/content-types/{name}` | the same, refused while an entry of the type exists in the account's tenants |

A tenant admin cannot change or delete the type: the account routes answer 403 to anyone who does
not manage the account, and `POST /api/content-types/{name}/fields` answers 403 for a name the
account owns. The other per-type tenant routes (`PUT .../public-delivery` and so on) look only at
the tenant's own types and answer 404.

A field added to the type applies to every tenant in the account at once, because there is one
definition. Existing entries are not rewritten, so a required field with no default is refused when
an entry of the type exists in the account's tenants. Changing or removing a field, and migrating
entries, need the versioned schema change of #934 and are not offered.

### A tenant's own type with the same name

A tenant may define its own types beside the account's. The same name is refused: creating it in a
tenant answers 409 while the account owns that name, and creating it on the account answers 409
while a tenant in the account has its own type of that name. A pair that already exists (a tenant
type created before the account type) is left alone, and the tenant's own definition wins for that
tenant.

### Where it is stored

An account-owned type is an `AccountContentType` document, single tenanted like `TenantAccount`. A
definition in one tenant's partition would be invisible to the others, and a copy per tenant is the
drift this removes. The document is read after the tenant's own definition and only when that finds
none. `Tenancy:DatabaseEnforcement` applies row level security to the conjoined tables only, so it
does not hide the document from a tenant. It does narrow the two cross-tenant checks (the name check
on create and the entries check on add field and delete) to the tenant the request is made on, which
is why the tenant-side create refusal exists too.

## Not in this slice

Account members and account-level roles beyond the manager grant, and an anonymous branding route
for the sign-in screen. For shared definitions: workflows, lifecycles, roles, the migration job of
#934, public delivery, numbering and uniqueness rules, and the token field type on an
account-owned type.
