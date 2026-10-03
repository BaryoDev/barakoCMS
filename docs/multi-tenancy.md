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

Domains are written with the tenant, `POST /api/tenants` and `PUT /api/tenants/{handle}`, and each
write clears the cached map, so a change routes on the next request. A domain belongs to one tenant;
a second claim is a 409. `GET /api/tenants/by-host/{host}` answers which active tenant a host
belongs to, anonymously and with the handle only, for a renderer serving several sites from one
process. It resolves the host the same way requests are routed: a registered domain first, then the
leading subdomain, so `acme.example.com` answers `acme` when an active tenant has that handle and no
domain row claims the host. An unknown or inactive handle is a 404 either way.

The same map can drive CORS and TLS, so adding a client's domain needs no config edit or restart.
`CORS:AllowTenantDomains` (off by default) allows a browser on `https://` plus a registered domain,
and `GET /api/tenants/tls-ask?domain={host}` answers a reverse proxy's on-demand TLS check. Both are
in [deploy-in-production.md](deploy-in-production.md#a-clients-own-domain-with-no-config-edit).

`RefuseUnknownHosts` turns a host that looks like a custom domain but matches nothing into a 404,
rather than quietly serving the default tenant. It is opt-in, because on a single-tenant deployment
every host is legitimately unrecognised.

Steps 3 and 4 serve whatever they resolve to, registered or not, unless `Tenancy:Mode` is `Multi`.
The next section is that setting.

## Tenancy mode

`Tenancy:Mode` says what kind of deployment this is. It has two values. A value that is neither
stops the host at startup with a message naming the setting, because the failure worth preventing
is an operator who mistyped `Multi` and runs a deployment that serves what they believe it refuses.
Change it by changing configuration and restarting.

### Single, the default

`Single` is what every deployment did before the setting existed, and what you get by leaving it
unset. Nothing is refused:

- a request that names no tenant is served from the `default` partition;
- a slug with no `Tenant` document is served as a partition of its own, and a token is issued for
  it without a membership check;
- registered tenants work as described on this page, with membership checked.

The name describes the deployment those two exemptions exist for (one site, nobody ever creates a
tenant). It does not stop a `Single` deployment from registering tenants, and `X-Tenant` is honoured
exactly as before.

### Multi

```json
{ "Tenancy": { "Mode": "Multi" } }
```

Opt-in. Every request and every token belongs to a registered, active tenant.

**Resolution refuses.** A request gets `404` with the plain text body
`This deployment serves registered tenants only.` when it resolves to:

- the `default` partition (no header, no registered domain and no subdomain, or `X-Tenant: default`);
- a slug with no `Tenant` document;
- a tenant whose `IsActive` is false.

The three get the same status and the same body, so the answer does not say which slugs were ever
registered. It is given by `TenantResolutionMiddleware`, before authentication and before any
endpoint. It runs before the CORS middleware, so a browser calling from another origin sees a
failed request and not the 404.

**These routes answer without a tenant**, and nothing else does. The list is in
`Infrastructure/Multitenancy/TenantlessRoutes.cs`, so a new route is refused until it is added.
Apart from `/api/auth/*`, each is allowed for `GET` and `HEAD` only:

| Route | Why |
| --- | --- |
| `/health`, `/health/live`, `/health/ready`, `/health/build` | a probe names no tenant |
| `/metrics` | a scraper names no tenant |
| `GET /api/meta` | a console reads the contract version before it knows a tenant |
| `GET /api/tenants/by-host/{host}` | how a renderer learns the tenant |
| `GET /api/tenants/{handle}/public` | how a sign-in page learns the tenant. It reads the registry on the `default` slug, then that tenant's published site entry |
| `GET /api/tenants/tls-ask` | how a reverse proxy asks whether to get a certificate for a host |
| `/api/auth/*` | identity is stored once for the deployment, and a provider redirects a social sign-in to one fixed address |

They run on the `default` slug whatever the request named, so a slug nobody registered never
reaches a session or an audit row through them. The public profile route opens one more session,
for the tenant the registry returned, and only after finding it registered and active. A CORS preflight (`OPTIONS` with `Origin` and
`Access-Control-Request-Method`) is let through as well, since a browser sends it without
`X-Tenant`; the CORS middleware answers it and no endpoint runs.

One consequence for the audit log. A sign-in or refresh sent with no tenant, or under an
unregistered or inactive one, is recorded with tenant `default`, since that is the slug the request
runs on. `GET /api/audit` shows a tenant administrator their own tenant's rows, so they do not see
those attempts, including failed sign-ins against their own members. A SuperAdmin does, with
`GET /api/audit?tenant=default`. A sign-in that names the tenant is recorded under it as before.

Left off on purpose: `/api/me/*` and `/api/tenants` (send the tenant you are signed in to),
`/swagger` (the delivery paths in the document are generated per tenant), the optional health
dashboard, and every module route, including the `POST /api/webhooks/resend` receiver. Reach those
on a tenant's host or with `X-Tenant`.

**No token for anything but a tenant.** `TokenIssuer` refuses the `default` partition and any slug
with no active `Tenant` document, and every path that issues an access token goes through it:
password sign-in, OTP, MFA, refresh, `POST /api/me/switch` and the ExternalAuth providers. Reaching
sign-in without a tenant is allowed; getting a token from it is not, and password sign-in answers
with the same `401` a wrong password gets. `POST /api/me/switch` answers `400` for such a target, as it does for
a tenant the caller does not belong to.

**A token issued before the switch stops working.** An access token lives fifteen minutes, so one
minted for the `default` partition or an unregistered slug just before `Multi` was turned on, or for
a tenant just before it was switched off, would otherwise still be accepted on the routes that do
not compare the token's tenant with the request's (`/api/me/*`, `/api/auth/*`, routes ending
`/public`). In `Multi`, `TenantAccessMiddleware` answers `403` on every route for a token whose own
`tenant` claim is not a registered, active tenant. It reads the cached list of active tenants, so
it follows that cache (below). A token with no `tenant` claim at all, which only tokens from before
the claim existed are, is not covered and passes as it does in `Single`.

**API keys.** A key carries its own tenant. In `Multi` a key scoped to the `default` partition or to
a slug with no active tenant is refused with `401`. Resolution runs before the key is read, so a
request with a key still has to name a registered, active tenant by host or `X-Tenant`. The key's
own tenant is the one the request then runs in, as before.

**Platform administration.** A SuperAdmin manages tenants from inside a registered tenant they
belong to: sign in there, and `/api/tenants` lists, creates and updates every tenant. Creating a
tenant makes the creator an Admin member of it, so they can sign in to it next. So **register at
least one tenant, and be a member of it, before turning `Multi` on**. With no active tenant every
route that needs one answers 404 and nobody can sign in. Nothing stored changes with the setting,
so setting it back to `Single` is the way out.

**Background work.** The workflow runner, the workflow run and webhook delivery retention sweeps,
the startup pass that encrypts stored workflow credentials and the startup notice about stored
validation rules visit registered tenants only, active or not, and not the `default` partition.
That is one query per registered tenant per pass, as with database enforcement
(`docs/tenancy-at-the-database.md`). The scheduled content sweep and the collection sync sweep
visit active tenants and skip the `default` partition. Not changed by the mode: the job queue,
which still runs a job already stored in the `default` partition (and, with database enforcement
off, in any partition), and the startup pass that redacts stored workflow logs, which still visits
the `default` partition. No pass logs what it skipped. The API logs one line at each start saying
the mode is `Multi` and which passes leave those partitions alone.

**The list of active tenants is cached** with the domain map, for `Multitenancy:CacheDuration`
(five minutes by default). Creating or updating a tenant through the API clears it on the instance
that handled the request, and a list that instance was still loading when the write landed is not
kept. Another instance keeps answering 404 for a new tenant, or serving one just switched off,
until its copy expires. The token issuer and the API key check read the registry each time.

### Data that is already there when you switch to Multi

Nothing is moved or deleted. Rows in the `default` partition, and rows under a slug with no
`Tenant` document, stay where they are. The API no longer reaches them, and the background passes
named above no longer visit them. For what is stored there that means:

- a workflow run that is pending or running does not execute, and stays as it is;
- scheduled content in the `default` partition is not published or unpublished when its time comes;
- finished workflow runs and webhook deliveries are no longer removed when their retention window
  passes, so they are kept until the partition is registered or the mode goes back to `Single`;
- credential parameters on stored workflows that were not yet encrypted stay as they were stored;
- a collection sync defined in the `default` partition no longer runs.

The log redaction pass is the exception: it still rewrites stored workflow logs in the `default`
partition at startup.

Run this before switching, in the API's database. It counts, for each partition no `Tenant`
document names, the content entries, the entries with a schedule still to run, and the workflow
runs that have not finished; `*DEFAULT*` is the `default` partition. With
`Tenancy:DatabaseEnforcement` on, run it as a superuser, since the policy hides other tenants'
rows from the application role.

```sql
with unregistered as (
    select 'content entries' as what, c.tenant_id
    from public.mt_doc_contents c
    union all
    select 'scheduled content entries', c.tenant_id
    from public.mt_doc_contents c
    where c.data ->> 'ScheduledPublishAt' is not null
       or c.data ->> 'ScheduledUnpublishAt' is not null
    union all
    select 'unfinished workflow runs', r.tenant_id
    from public.mt_doc_workflow_runs r
    where (r.data ->> 'Status')::integer in (0, 1)
)
select u.what, u.tenant_id, count(*) as rows
from unregistered u
where not exists (
    select 1 from public.mt_doc_tenants t where t.data ->> 'Slug' = u.tenant_id
)
group by u.what, u.tenant_id
order by u.tenant_id, u.what;
```

Other tenant-scoped tables (workflow definitions, files, webhook deliveries) hold rows too. The
same `not exists` over one of them answers for that table.

To bring an unregistered partition back, create a tenant with that slug (`POST /api/tenants`) and
give its users memberships. That works only for a slug that is a valid handle. For the `default`
partition, and for a slug that is not a valid handle, there is no tool: run in `Single` while you
move the data yourself.

## Tokens and access

A token carries `UserId`, the roles resolved for that tenant, and a `tenant` claim
(`Infrastructure/Auth/TokenIssuer.cs`).

**The membership check runs when a token is issued, not on every request.**
`TokenIssuer.CheckTenantAccessAsync` refuses to mint a token when the tenant is registered and the
user has no active `Membership` on it. In `Single`, which is the default, two cases skip the check
on purpose, and both are documented at the call site:

- the **default tenant**, which has no membership rows by design;
- an **unregistered slug**, which is a single-tenant deployment reached over a subdomain that nobody
  ever created a `Tenant` document for. Denying it locks out the whole deployment, which is what
  happened the first time this check shipped.

With `Tenancy:Mode` set to `Multi` neither is skipped: both are refused before the membership check
(see Tenancy mode above).

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
- `POST /api/me/switch` exchanges the caller's access token for one scoped to another tenant they
  belong to. The new token expires when the presented one would have, and the presented one is
  revoked. It returns no refresh token (`refreshToken` is empty): the one from sign-in keeps
  working, since a refresh mints for the `X-Tenant` it is sent and re-checks membership.
  The body names the target as `{ "tenant": "<handle>" }`. `club` is an alias of `tenant`, kept for
  clients written when it was the only name, and both go through the same membership check. A
  request that sets both to different tenants is a 400, and so is one that sets neither. Only the
  JSON body is read: a `tenant` or `club` on the query string is ignored.
- `/api/tenants` creates, lists and updates tenants, gated on `SuperAdmin`. A tenant here is its
  handle, name, domains and active flag.
- `GET /api/tenants/{handle}/public` is the anonymous lookup a sign-in page needs.

## The tenant profile

A tenant used to carry a public profile on its own record: `LogoUrl`, `About`, `Location`,
`LocationUrl`, `SocialHandle`, `Email` and `ContactUrl`. From 4.6.0 the tenant's `site` entry holds
it ([site-settings.md](site-settings.md)), so a site is described in one place and gains a field
without a change to the API.

| Was on the tenant | Is the site field |
| --- | --- |
| `LogoUrl` | `Logo` |
| `About` | `About` |
| `Location` | `Location` |
| `LocationUrl` | `LocationUrl` |
| `SocialHandle` | `SocialHandle` |
| `Email` | `Email` |
| `ContactUrl` | `ContactUrl` |

**Reading.** `GET /api/tenants/{handle}/public` keeps its shape, and `GET /api/me/tenants` keeps
`logoUrl`. Both read the tenant's site entry the way `GET /api/public/site` delivers it: the
published entry of a publicly deliverable type, and only fields the type marks Public. A draft is
not read. `GET /api/tenants` and the answers to a tenant create or update no longer carry the
profile. Each field is answered on its own:

| The tenant's site | The field answers |
| --- | --- |
| No published entry, or the type does not declare the field | the value on the tenant record |
| The type declares the field and it is not Public | empty |
| Declared, Public, and the entry holds it, blank included | the entry's value, so a blank answers empty |
| Declared, Public, and the entry has no key for it | the value on the tenant record |

So the site decides wherever it has said something. Marking a field Sensitive takes it off the
route, and blanking it in the entry removes it, whatever the tenant record still holds. The last
row is a field nobody has set, such as `Logo` on a site made before the move, and is what keeps a
tenant answering as it did before the migration below has run. What differs from 4.5 without the
migration is a tenant whose site already said something about `Logo`, the one field it had: it
answers with the site's logo, or with none where the site's is blank or not Public.

`logoUrl`, `locationUrl` and `contactUrl` answer a site value only when it is an absolute `http` or
`https` address, and empty otherwise. The tenant API used to hold its two links to that on write. A
site field can be declared as plain text by the tenant's own administrator, so the rule is applied
on read. A value from the tenant record is answered as stored, as before.

**Writing.** `POST /api/tenants` and `PUT /api/tenants/{handle}` refuse a request that sets one of
the seven to a value, with a 400 naming the site field. The check runs before the handle is looked
up, so a value sent to a handle that does not exist is a 400 and not a 404. Absent and `null` mean
not sent, and an update leaves what is on the tenant record alone. An empty string in an update
blanks that field on the tenant record, which is how the platform removes a value the migration
left behind. The profile is edited as content: update the `site` entry and publish it.

**Who can change it.** That changed. The tenant API needs the `manage_tenants` capability, which a
SuperAdmin holds, so only the platform could set a profile. The site entry is content of the
tenant, so anyone who may update and publish the `site` type in that tenant can, such as its own
Admin. A SuperAdmin still can, from inside the tenant. Who can read it did not change: it was
anonymous, and a published site entry is.

**Moving what is stored.** `migrations/4.6.0/tenant-profile-to-site.sql` moves each value into the
site entry and blanks it on the tenant record. It moves a tenant only when that tenant has one
published, Public entry of a publicly deliverable `site` type that is not event sourced, and it
prints a `NOTICE` naming every tenant and value it leaves behind. Where the entry already holds a
different value the entry wins: both sides are kept, the `NOTICE` names the tenant and field, and the
API answers with the entry's value. A field the type declares and the entry holds blank was cleared
by an editor, so the value is not copied into it. A value is also left where it is when the site
field is not Public, is not a text or url field, or is a url field and the value is not an `http`
or `https` address. Run the file again after a tenant it left alone publishes its site entry.
Setting `barako.only_tenant` to a handle makes it look at that tenant only; the file's header shows
how.

A value left on a tenant record is removed with `PUT /api/tenants/{handle}` and an empty string in
that field. The file cannot tell an entry whose key was removed from one that never had it: both
read as never set, and the value is moved in. This lists what is still on tenant records:

```sql
select t.data ->> 'Slug' as tenant, k.key as field
from public.mt_doc_tenants t
cross join lateral jsonb_each_text(t.data) k
where k.key in ('LogoUrl', 'About', 'Location', 'LocationUrl', 'SocialHandle', 'Email', 'ContactUrl')
  and btrim(coalesce(k.value, '')) <> ''
order by 1, 2;
```

`Branding` is not moved. It was never writable through the API and has no fixed shape to map to a
site field, so `GET /api/tenants` and `GET /api/me/tenants` still return it from the tenant record.

The fallback, the seven members and `Branding` on `Tenant` are marked obsolete and go in 6.0. Before
then the query above has to come back empty, or the values it lists that are still being served
stop being served.

A host or module that reads those members from a `Tenant` in its own code finds them empty for a
tenant the file moved. 4.6.0 does not need the file to have run, so it can wait until that code
reads the site entry.

barakoBrew has a tenant switcher built on these.

## Isolation, and its limits

1. Marten's conjoined tenancy auto-filters every query. This is the guarantee everything else backs
   up.
2. Tokens are tenant-scoped and a mismatch is refused.
3. Membership is checked at token issue. With `Tenancy:Mode` set to `Multi`, the default partition
   and unregistered slugs are refused there and at resolution.
4. Cross-tenant tests run in CI: `TenantIsolationTests`, `CrossTenantContentApiTests`,
   `CrossTenantTokenTests`, `TenantResolutionTests` and
   `Features/Workflows/WorkflowTenantIsolationTests`. They assert that one tenant's token and
   queries return nothing from another's.

**Postgres row-level security is available and off by default**, as `Tenancy:DatabaseEnforcement`
(#446). Off, which is the default, a slipped application-layer filter has nothing underneath it. On,
one tenant's session cannot read or write another's even when the filter is missed, enforced by the
database. The boundary is `DECISIONS.md` D11: authorisation stays in the application, and the
database enforces tenancy and nothing else.

Two limits worth knowing before relying on it. It does not catch a session opened with no tenant at
all, because Marten represents that as the default tenant and Postgres cannot tell it from meaning
the default partition. And it does not cover `mt_events` or `mt_streams`, which stay
application-filtered.

Turning it on is not a settings change: it needs a connection role that is not a superuser, since a
superuser bypasses row level security entirely. `docs/tenancy-at-the-database.md` has the steps and
the two deployment constraints.

The honest trade-off of a shared database is that a serious bug's blast radius is every tenant.
Database-per-tenant is the same Marten API and remains the escape hatch for anyone who needs hard
isolation, but nothing here has been exercised that way.
