# Multi-tenancy

One deployment, one database, many tenants. A user is global and signs in once; what they may do
depends on which tenant the request resolved to.

This describes what is in the code. Where a decision came out differently from the original design,
the code is what is written down here.

## Model

- **One database, one app.** Marten stamps a tenant id on every tenanted document and filters every
  query by the session's tenant, so isolation is a property of the session rather than a `WHERE`
  clause somebody has to remember.
- **One global identity.** A `User` exists once across all tenants (`Models/User.cs`).
- **Membership carries per-tenant roles.** `Membership` links a user to a tenant slug with the roles
  they hold there (`Models/Membership.cs`). Note it stores `TenantSlug`, a string, not a tenant id.
- **A `default` tenant.** `Tenant.DefaultSlug` is `"default"`, and `TenantSessionFactory` opens a
  tenant-less session for it, so a single-tenant deployment keeps working with no tenant rows and no
  migration.

## What is global and what is tenant-scoped

`options.Policies.AllDocumentsAreMultiTenanted()` makes everything tenanted by default
(`Extensions/ServiceCollectionExtensions.cs`), and `options.Events.TenancyStyle = Conjoined` does the
same for the event store. Documents then opt **out** one by one.

| Global (`SingleTenanted`) | Tenant-scoped (the default) |
| --- | --- |
| `User` | `Content`, `ContentTypeDefinition` |
| `Tenant` (the registry itself) | `StoredFile`, `FileBlob` |
| `Membership` (necessarily cross-tenant) | `Account`, `JournalEntry`, `NumberSequence` |
| `Role` | workflows and their events |
| `RefreshToken`, `RevokedToken`, `OtpCode`, `MfaSecret`, `ApiKey` | |
| `Device` | |
| `AuditEvent` | |

Two of those are worth calling out because the obvious guess is the other way round.

**Roles are global, not per tenant.** A role is a named permission set; which roles a person holds
*in a tenant* is what `Membership.RoleIds` carries. Defining the role once and assigning it per
tenant is the split, rather than every tenant owning its own copy of "Editor".

**Auth artifacts are global.** A refresh token, an OTP code, an MFA secret, an API key and a trusted
device all belong to the global identity, not to the tenant whose subdomain happened to be in the URL
when they were created. A device trusted once stays trusted; a revoked token is revoked everywhere.

Changing `Events.TenancyStyle` on an existing store is not a live migration. The comment at the
configuration site says so, and it is the reason this is settled rather than adjustable.

## Tenant resolution

`TenantResolutionMiddleware` resolves, in order:

1. the **`X-Tenant` header**,
2. a **registered custom domain** (`Tenant.Domains`, looked up through `ITenantDomainSource`),
3. the host's **leading subdomain**, ignoring the infra labels `www`, `app`, `api` and `admin`,
4. the **`default` tenant**.

**The header is accepted from any caller, deliberately.** That is how path-based routing works: the
front end sets `X-Tenant` from the URL handle. Naming a tenant is not the same as reaching its data,
because an authenticated request still has to survive `TenantAccessMiddleware`, and anonymous
requests only ever reach content a tenant published. Forging the `Host` header selects exactly the
same set of tenants by a longer route, which is why #147 closed as not-an-escalation.

`RefuseUnknownHosts` turns a host that looks like a custom domain but matches nothing into a 404,
rather than quietly serving the default tenant. It is opt-in, because on a single-tenant deployment
every host is legitimately unrecognised.

## Tokens and access

A token carries `UserId`, the roles resolved for that tenant, and a `tenant` claim
(`Infrastructure/Auth/TokenIssuer.cs`).

**The membership check runs when a token is issued, not on every request.**
`TokenIssuer.CheckTenantAccessAsync` refuses to mint a token when the tenant is registered and the
user has no active `Membership` on it. Two cases skip the check on purpose, and both are documented
at the call site:

- the **default tenant**, which has no membership rows by design;
- an **unregistered slug**, which is a single-tenant deployment reached over a subdomain that nobody
  ever created a `Tenant` document for. Denying it locks out the whole deployment, which is what
  happened the first time this check shipped.

`TenantAccessMiddleware` then compares the token's `tenant` claim to the resolved tenant on each
request and returns 403 on a mismatch. It exempts `/api/me/*` (a user has to be able to list and
switch tenants from anywhere) and routes ending `/public`. A token with no `tenant` claim passes
through, which is what keeps tokens issued before the claim existed working.

## Roles at request time

`PermissionResolver` asks `MembershipRoles.EffectiveRoleIdsAsync` for the caller's roles, which is
the **union** of the user's global `User.RoleIds` and their membership roles in the current tenant.

`User.RoleIds` was kept rather than moved. A platform SuperAdmin stays a SuperAdmin inside every
tenant, which is what makes the platform-global screens keep working after switching in, and a
deployment with no memberships at all behaves exactly as it did before multi-tenancy existed.

## Endpoints

- `GET /api/me/tenants` lists the caller's tenants (`Features/Me/MyTenantsEndpoint.cs`).
- `POST /api/me/switch` issues a token for another tenant the caller belongs to.
- `/api/tenants` creates, lists and updates tenants, gated on `SuperAdmin`.
- `GET /api/tenants/{handle}/public` is the anonymous lookup a sign-in page needs.

The admin UI has a tenant switcher built on these.

## Isolation, and its limits

1. Marten's conjoined tenancy auto-filters every query. This is the guarantee everything else backs
   up.
2. Tokens are tenant-scoped and a mismatch is refused.
3. Membership is checked at token issue.
4. Cross-tenant tests run in CI: `TenantIsolationTests`, `CrossTenantContentApiTests`,
   `CrossTenantTokenTests`, `TenantResolutionTests` and
   `Features/Workflows/WorkflowTenantIsolationTests`. They assert that one tenant's token and
   queries return nothing from another's.

**Postgres row-level security is not implemented.** It was the intended defence-in-depth backstop
and there is nothing in the schema or the code that does it, so a slipped application-layer filter
has nothing underneath it. What the boundary is has since been settled in `DECISIONS.md` D11:
authorisation stays in the application, and the database enforces tenancy and nothing else. The
policies on `tenant_id`, behind a flag and with a startup assertion so a new module's table cannot
be silently unprotected, are issue #446.

The honest trade-off of a shared database is that a serious bug's blast radius is every tenant.
Database-per-tenant is the same Marten API and remains the escape hatch for anyone who needs hard
isolation, but nothing here has been exercised that way.
