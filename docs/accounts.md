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

## Not in this slice

Account members and account-level roles beyond the manager grant, and an
anonymous branding route for the sign-in screen.
