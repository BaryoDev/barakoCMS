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
an entry of the type exists in any of the account's tenants. Two managers adding fields at once
cannot lose one: the loser gets 409 and reads the type again. The add is also refused with 409
while a field change runs on that name in any of the account's tenants. Changing or removing a
field, and migrating entries, need the versioned schema change of #934 and are not offered.

The default account is an account like the others. A type owned by it is shared by every tenant
with no `AccountId`, including the default tenant, so give a type to a real account unless that
is what you want.

### Rules on an account type

- `visibleToRoles` takes platform roles only. A tenant role would name a different role, or none,
  in every tenant but the manager's, so the request answers 400.
- A Sensitive or Hidden field is refused (409) when any tenant of the account has decided that name
  as event sourced, on create and on add field, because an event sourced type cannot hold one.
- A token field is refused.
- A tenant cannot move out of an account while it holds entries of that account's types (409), and
  cannot move into one that owns a name the tenant already has its own type for (409).

### A tenant's own type with the same name

A tenant may define its own types beside the account's. The same name is refused: creating it in a
tenant, applying a blueprint that brings it, and importing a bundle that carries it all answer an
error while the account owns that name, and creating it on the account answers 409 while a tenant
in the account has its own type of that name. A pair that already exists (a tenant type created
before the account type) is left alone, but the account's definition wins: every reader below
resolves the account's first, so the tenant's copy cannot loosen its sensitivity or
`visibleToRoles`.

### Where it is stored

An account-owned type is an `AccountContentType` document, single tenanted like `TenantAccount`,
stored with optimistic concurrency. A definition in one tenant's partition would be invisible to the
others, and a copy per tenant is the drift this removes. `Tenancy:DatabaseEnforcement` applies row
level security to the conjoined tables only, so it does not hide the document from a tenant. It does
narrow a cross-tenant query on a conjoined table to the session's tenant, so every check across the
account's tenants (the name check, the entries count, the event sourced check, the field change
check) opens a query session per tenant id and never treats a tenant it cannot read as empty.

### Readers that use the account definition

`AccountContentTypes.ResolveAsync` and `ResolveManyAsync` (account first, then the tenant's own) are
used by entry validation, create, update, the sensitivity service, the sensitive read audit on get,
list and history, list search and field filters, rollback search text, and the diagnostics
redactor (which also names every account type's non-Public fields).

Not yet covered, and still reading only the tenant's own types: entry files, status change, counts,
get by slug, token search, collection export and push, the data seeder, public delivery, the
sitemap, and the import's own per-record check (it reports an entry of an account type as having no
type, though the write itself validates against the account's).

## Not in this slice

Account members and account-level roles beyond the manager grant, and an anonymous branding route
for the sign-in screen. For shared definitions: workflows, lifecycles, roles, the migration job of
#934, public delivery, numbering and uniqueness rules, and the token field type on an
account-owned type.
