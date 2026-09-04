# Delivering a client project on barakoCMS

The path from a clean machine to a client site you have handed over. Every other document here
answers what barakoCMS can do. This one answers what you do, in what order, and where it goes wrong.

It is written for the case barakoCMS is shaped for: one deployment, one database, several client
accounts on it. A tenant is not a technical partition, it is a client account, so most of this
document is about getting one tenant into the right state and doing it again for the next client.

If you are running a single client on a dedicated deployment, skip step 3. Everything runs on the
implicit `default` tenant with no tenant row, exactly as it did before multi-tenancy existed.

Step 5 still applies. `POST /api/tenants/members` is the only administrative way to create a user:
`Features/Users/` can list, assign roles and groups and reset a password, and cannot create. The only
other route that makes one is the anonymous `POST /api/auth/register`. On a single-client deployment
the member endpoint operates on `default` and works exactly the same way, because the tenant context
falls back to the default slug and token issuing does not require a membership row for it.

Read [multi-tenancy.md](multi-tenancy.md) before this if you have not. It sets out what is per tenant
and what is shared, and two of the answers are the opposite of the obvious guess.

## The shape of the work

| Step | What you do | Where |
| --- | --- | --- |
| 1 | Stand an instance up | Docker, one compose file |
| 2 | Decide who the platform admin is | `InitialAdmin`, seeded on first boot |
| 3 | Create the tenant | `POST /api/tenants` |
| 4 | Model the content, inside that tenant | `POST /api/content-types` |
| 5 | Add the client's people and pick their roles | `POST /api/tenants/members` |
| 6 | Point a frontend at the delivery API | `GET /api/public/{type}` |
| 7 | Deploy behind a domain | `docker-compose.prod.yml` |
| 8 | Hand over | export, credentials, and what stays yours |

Steps 3 and 4 are in that order for a reason. Content types are tenant-scoped documents. Modelling
first and creating the tenant afterwards leaves the model on `default`, where the client's tenant
cannot see it, and moving it then needs the Portability module. Create the tenant first, switch into
it, and model there.

---

## 1. Stand an instance up

Local first. [`quickstart/README.md`](../quickstart/README.md) brings up the API and Postgres from
published images with no build step:

```bash
cd quickstart
cp .env.example .env
# at minimum: DB_PASSWORD, JWT_KEY (32+ characters), ADMIN_USERNAME, ADMIN_PASSWORD
docker compose up -d
```

API on `http://localhost:5005`, health on `http://localhost:5005/health`. The console is
[barakoBrew](https://github.com/BaryoDev/barakoBrew), in its own repository; it runs against this
same image.

The quickstart image is the full suite, so every module is present and each one stays off or on a
mock until you give it keys. That is the right default while you are still finding out what the
client needs. Section 9 covers what picking a smaller set costs you later.

Two settings are worth deciding now rather than at deploy time.

A note on how to set any of them. The names below are configuration keys, and the `__` form is how
the .NET configuration provider reads them from the environment. That does not mean putting them in
`.env` works: a compose file passes through the variables it names and nothing else. `.env` reaches
the app only for a variable the compose file interpolates into the `app` service's `environment:`
list. Everything else is an edit to that list. `quickstart/docker-compose.yml` and
`docker-compose.prod.yml` name different sets, so check the one you are running.

**`Seed:DemoContent`** (env `Seed__DemoContent`) decides whether the instance seeds a demo
`AttendanceRecord` content type, three sample records and a workflow that sends mail. Unset, it
follows the environment: on in Development, off everywhere else. The quickstart and the production
compose both run as Production, so a client instance does not start with demo fixtures unless you ask
for them.

**`Swagger:Enabled`** (env `Swagger__Enabled`) decides whether `/swagger/v1/swagger.json` is served.
Default is on in Development and off elsewhere. You want it on somewhere, because that document
carries the tenant's own delivery paths and is what a generated frontend client is built from. See
step 6.

Somewhere, not necessarily here. The document is anonymous: the flag is the only thing in front of
it, and a GET on `/swagger/{doc}/swagger.json` is rewritten for whatever `X-Tenant` it carries with
no check on who is asking. Turning it on publishes the full route surface, every admin route
included, plus that tenant's content model, to anyone who can reach the API. Generate the client from
a staging instance, or put `/swagger` behind the reverse proxy, rather than leaving it on in front of
a client's production deployment.

## 2. Decide who the platform admin is

The seeder creates four global roles on first boot: `SuperAdmin`, `Admin`, `HR`, `User`. It creates
the initial account from the `InitialAdmin` section (`ADMIN_USERNAME` / `ADMIN_PASSWORD` in the
quickstart, `ADMIN_USER` / `ADMIN_PASSWORD` in production).

That account is the platform administrator. It is yours, not the client's, and step 8 says why.

Roles are global documents, not tenant-scoped. `Role` is `SingleTenanted` and a membership carries
which of those roles a person holds in a tenant. This has one consequence that catches people out
across a multi-client deployment: **a role you create for one client is visible to every client**.
`GET /api/tenants/members/roles` lists every role in the deployment, minus SuperAdmin. Name roles so
that seeing them is not itself a disclosure, and remember that a role's content permissions name
content type slugs, so a role built for one client grants the same thing in any tenant that happens
to have a type by that name.

Two more naming rules that are not cosmetic:

- Do not hand a client's staff the seeded `Admin` role. The capability gate honours the role names it
  replaced while `Auth:LegacyRoleFallback` is true, which is the default, so a caller holding a role
  called `Admin` opens every gate that lists `Admin` as a fallback, whatever capabilities you did or
  did not give it. You cannot create a second role with one of those names anyway: role names are
  unique and the seeder has already taken `Admin`, `SuperAdmin`, `HR` and `User`. The risk is
  assigning the existing one, not minting a new one.
- Once your roles carry capabilities, set `Auth:LegacyRoleFallback=false` (env
  `Auth__LegacyRoleFallback`) and the names stop meaning anything on their own.
  [access-control.md](access-control.md) has the migration table.

## 3. Create the tenant

Platform admin only. `POST /api/tenants` requires the `manage_tenants` capability, which only
SuperAdmin holds by default.

```bash
API=http://localhost:5005

TOKEN=$(curl -s -X POST "$API/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"YOUR_ADMIN_PASSWORD"}' \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')

curl -s -X POST "$API/api/tenants" \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{
        "handle": "acme",
        "name": "Acme Corporation",
        "logoUrl": "https://acme.example.com/logo.png",
        "about": "We make everything.",
        "location": "Koronadal",
        "locationUrl": "https://maps.example.com/acme",
        "socialHandle": "@acme",
        "email": "hello@acme.example.com",
        "contactUrl": "https://facebook.com/acme",
        "isActive": true
      }'
```

The handle is 3 to 40 characters of `a-z`, `0-9` and hyphens, and some are reserved. `contactUrl`
and `locationUrl` must be full `http(s)` URLs or the request is rejected.

Creating the tenant provisions **you** as an active `Admin` member of it in the same transaction.
Without that, the tenant would exist with no memberships and the token issuer would refuse a token
for it to everyone including its creator.

The profile fields are what `GET /api/tenants/{handle}/public` serves anonymously, which is what a
client's sign-in page or landing page reads. That endpoint needs no token.

### Getting into the tenant

Every authoring call after this has to be made *inside* the tenant. Two things have to line up: the
token has to be minted for that tenant, and the request has to resolve to it.

Resolution order is the `X-Tenant` header, then a registered custom domain, then the host's leading
subdomain (ignoring `www`, `app`, `api` and `admin`), then `default`.

From curl, use the header on both the login and the call:

```bash
TENANT=acme

TOKEN=$(curl -s -X POST "$API/api/auth/login" \
  -H "X-Tenant: $TENANT" -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"YOUR_ADMIN_PASSWORD"}' \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')

curl -s "$API/api/content-types" -H "Authorization: Bearer $TOKEN" -H "X-Tenant: $TENANT"
```

A token minted for one tenant and sent with another tenant's header is refused with 403 by
`TenantAccessMiddleware`, so the two cannot drift apart silently.

From barakoBrew, you sign in and then switch. The console derives `X-Tenant` from the token's own
`tenant` claim, so at the login screen there is no header at all and you land on whichever tenant the
host resolved to. The tenant switcher then calls `GET /api/me/tenants` and `POST /api/me/switch` to
swap your token for one scoped to the tenant you picked.

One wart to know before you write a script against it: the switch request field is spelled `club`,
not `tenant`.

```bash
curl -s -X POST "$API/api/me/switch" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"club":"acme"}'
```

### Custom domains and branding are not settable through the API

`Tenant` carries a `Domains` list and a `Branding` dictionary. Both are returned by
`GET /api/tenants` and `GET /api/me/tenants`. Neither can be written: the create and update request
bodies have no field for them, and no other endpoint sets them.

So if a client is to be reached on their own domain rather than a subdomain, that row goes into the
database directly. Store the bare host, for example `acme.com`. A leading `www.` is ignored on both
sides of the match. The domain map is cached for `Multitenancy:CacheDuration`, five minutes by
default, and nothing in the running application invalidates it, so allow for that after the write.

`Multitenancy:RefuseUnknownHosts` turns a host that matches no tenant into a 404 instead of quietly
serving the default tenant. It is off by default because a single-tenant deployment legitimately
answers on a host nobody registered. Turn it on once every client has a domain row.

## 4. Model the content, inside the tenant

Content types are tenant-scoped, so this is done while switched into the client's tenant. There is no
schema import screen in the admin, so a schema file is posted to the API. The worked example is
[`examples/blog-starter`](../examples/blog-starter/), which is a real blog schema and a frontend that
reads it.

```bash
curl -s -X POST "$API/api/content-types" \
  -H "Authorization: Bearer $TOKEN" -H "X-Tenant: $TENANT" \
  -H 'Content-Type: application/json' \
  --data-binary @examples/blog-starter/blog-schema.json
```

Or build it in the admin under **Content Types**. The admin's field editor sets a field's
sensitivity at the point the type is created.

Three decisions in this step are worth making deliberately.

**Public delivery is opt in.** `isPubliclyDeliverable` defaults to false. Without it,
`/api/public/{type}` returns 404 whatever the entries underneath say. The switch is on the create
body, on the toggle in the admin's content type screen, and on
`PUT /api/content-types/{name}/public-delivery`. Setting `PublicDelivery:RequireAcknowledgement` to
true makes the enable call refuse unless it carries `acknowledgeExposure`, and the refusal names how
many published entries the decision would expose. It defaults to false.

**A slug field is what makes `/api/public/{type}/{slug}` exist.** A field of type `slug`, or failing
that a field named `slug`. Without one that route is 404.

**`eventSourced` is permanent.** It defaults to false, which is every content type that exists today:
the document is the source of truth and events are still appended for history and workflows. Setting
it true makes the event stream authoritative, and the choice is recorded against the *name*, so it
survives deleting the type and creating it again. [event-sourced-content-types.md](event-sourced-content-types.md)
has what that commits you to.

There is no general content-type update endpoint and no delete endpoint. What can be changed after
the fact is exactly three things: public delivery, one field's sensitivity
(`PUT /api/content-types/{name}/fields/{field}/sensitivity`, admin only, and lowering a level requires
`acknowledgeDisclosure` because it is retroactive), and a search-text rebuild
(`POST /api/content-types/{name}/rebuild`). Field names, field types and reference targets are
load-bearing once entries exist. Model as if the shape is close to final, because in practice it is.

### Reusing a model across clients

If several clients get the same shape, keep the house model on the `default` tenant and move it with
the Portability module rather than maintaining the JSON by hand:

- `GET /api/portability/export` downloads content types and their content as one JSON bundle.
- `POST /api/portability/import` applies a bundle **into the calling tenant**. A bundle carries no
  tenant identity of its own, which is what makes it safe to move.

Content types are upserted by name, so re-importing an evolved bundle updates rather than duplicates
them. Entries in the same bundle do not: every record in the import gets a fresh id, so re-importing
into a tenant that already has them adds a second copy of each. Import a bundle's entries once, into
a new tenant, and evolve the type from then on.
Content is recreated through events, so imported entries have real history.

Treat a bundle as sensitive. It contains whatever the content contains, and it leaves the system's
access control behind the moment it is downloaded.

## 5. Add the client's people, and pick their roles

This is the step that was not writable until recently. It is now five endpoints, all scoped to the
caller's *current* tenant rather than one named in the path, and all gated on
`manage_tenant_members`, which SuperAdmin and Admin hold by default.

| Method | Route | What it does |
| --- | --- | --- |
| GET | `/api/tenants/members` | the roster, newest first, paginated |
| POST | `/api/tenants/members` | add a person by email, with roles |
| PUT | `/api/tenants/members/{userId}` | change their roles or status |
| DELETE | `/api/tenants/members/{userId}` | mark them removed |
| GET | `/api/tenants/members/roles` | the roles you may assign in a tenant |

barakoBrew has all of this on the Tenants screen.

```bash
curl -s -X POST "$API/api/tenants/members" \
  -H "Authorization: Bearer $TOKEN" -H "X-Tenant: $TENANT" \
  -H 'Content-Type: application/json' \
  -d '{"email":"editor@acme.example.com","roleIds":["..."]}'
```

What happens on that call:

- If no user has that email, one is created with **no password**. Their username is the email address,
  or the email plus a suffix if that username is already taken.
- If the user already exists anywhere in the deployment, they are linked, not duplicated. A user is a
  global identity.
- Re-adding somebody previously removed reactivates their existing membership row rather than
  creating a second one, so their join date and their history survive.
- SuperAdmin cannot be granted here. It is a platform role, and handing it out through a per-tenant
  surface would let an administrator of any tenant mint themselves platform access.
- A removal marks the row `Removed`. Nothing is deleted, because the audit trail and a later re-add
  both read it.

### How the client's person actually signs in the first time

An invited account has no password, so password login will not work for them. They sign in with an
emailed code:

```http
POST /api/auth/otp/request   { "email": "editor@acme.example.com" }
POST /api/auth/otp/verify    { "email": "editor@acme.example.com", "code": "123456" }
```

The admin's login screen has this as **Email me a sign-in code**. `otp/request` always answers the
same way whether or not the address exists, so a failure to send shows up in the logs and not in the
response. Verify mints the same token pair password login does, and the token issuer still checks
membership, so proving control of the mailbox says who you are and not which tenants you may enter.

Two things follow from this that you should tell the client up front:

- **Email has to work before anybody can be invited.** Without a provider module registered and
  credentialled, the core registers a mock that logs and delivers nothing. Register
  `ResendEmailModule` (the suite image already has it) and set the key, either as `RESEND_API_KEY` or
  through Settings, Email in the admin. [configuring-email.md](configuring-email.md) covers where each
  value comes from and why the key can never be read back.
- **An invited member cannot set their own password.** `POST /api/me/password` verifies the current
  password before changing it, and they have none. Either they keep using the emailed code, or a
  SuperAdmin sets one for them with `POST /api/users/{userId}/password`, which needs the
  `manage_users` capability that only SuperAdmin holds by default.

### Which role to give them

Do not hand a client's staff the seeded `Admin` role. It carries `manage_tenant_members`,
`manage_user_membership`, `manage_user_groups`, `manage_api_keys` and `view_audit_log`, and two of
those reach past their tenant. Section 10 names both.

Build a role for the client instead, out of content permissions and nothing else. `POST /api/roles`
and `PUT /api/roles/{id}` take the same body and need the `manage_roles` capability, which only
SuperAdmin holds by default, so this is yours to do rather than theirs:

```json
POST /api/roles
{
  "name": "Acme Editor",
  "description": "Writes and publishes Acme's blog posts",
  "systemCapabilities": [],
  "permissions": [
    { "contentTypeSlug": "blog-post",
      "read":   { "enabled": true },
      "create": { "enabled": true },
      "update": { "enabled": true },
      "delete": { "enabled": false } }
  ]
}
```

Permissions are additive across a user's roles, granted if any role allows. Row-level scope is
Directus-style conditions on the rule, so "a member reads only their own rows" is
`{"read": {"enabled": true, "conditions": {"$createdBy": {"_eq": "$CURRENT_USER"}}}}`. Field-level
masking is separate again and lives on the content type's schema.
[access-control.md](access-control.md) is the full picture of all three layers.

Then assign that role through the membership, not through the user:
`POST /api/tenants/members` on the way in, `PUT /api/tenants/members/{userId}` afterwards.

Know what a custom role still cannot reach. Only the surfaces listed in
[access-control.md](access-control.md)'s migration table gate on a capability. Everything else is
still a `Roles(...)` gate on the seeded names, so a role called `Acme Editor` cannot create a content
type, toggle public delivery, change a field's sensitivity, or export a Portability bundle, whatever
capabilities you put on it. Those stay yours until #443 finishes the migration. Content itself is
different: the content endpoints go through the permission resolver, so a custom role reads, writes
and publishes entries exactly as its permissions say.

## 6. Point a frontend at the delivery API

The client's public site reads [the anonymous delivery API](delivery-api.md). No token, no cookie, no
API key. It is a separate surface from `/api/contents`, which is the authoring API and requires a
bearer token.

```http
GET https://api.example.com/api/public/blog-post?page=1&pageSize=20&sort=-publishedAt
GET https://api.example.com/api/public/blog-post/hello-world
GET https://api.example.com/api/public/blog-post/search?q=marten
GET https://api.example.com/api/public/blog-post/feed.xml
GET https://api.example.com/api/public/sitemap.xml
```

Three things have to be true before an entry is delivered, and no query parameter turns any of them
off: the type is marked publicly deliverable, the entry's status is `Published`, and the entry's
document sensitivity is `Public`. Any field not marked `Public` is stripped from the payload. Filters
and sorts work only on public fields, and naming any other field is a 400 rather than being ignored,
because filtering on a field you cannot read is an oracle.

Multi-tenant note: delivery resolves the tenant the same way everything else does. A frontend on a
custom domain or a subdomain needs nothing extra. A frontend on a shared host has to set `X-Tenant`
itself. That header is accepted from any caller on purpose, and it is how path-based routing works:
naming a tenant is not the same as reaching its data, because anonymous callers only ever see what a
tenant published.

**CORS is where this goes wrong first.** The browser origin serving the site has to be in the API's
allowed origins (`ALLOWED_ORIGINS` in the quickstart, `FRONTEND_ORIGINS` in production). If it is
not, the fetch fails in the browser while the same URL works in curl, and it looks like the API is
down.

**Do not render richtext through `innerHTML`.** The CMS stores it verbatim. The blog-starter example
uses `textContent` and says why.

### Images and files

The Files module is how a site serves images. `POST /api/files` is a multipart upload; send
`isPublic=true` in the form to make the file anonymously readable, because the default is private and
fails closed. The frontend then reads `GET /api/public/files/{id}`, which is anonymous, cached for a
day, and 404s for anything not marked public so private ids cannot be probed. With an object store
configured it redirects to the object's URL; on Postgres storage it proxies the bytes.

Three limits to plan around before you promise the client an image workflow:

- **10 MB per file**, and only `image/png`, `image/jpeg`, `image/gif`, `image/webp`, `image/avif` and
  `application/pdf`. SVG is deliberately not on the list, because a public SVG opened directly would
  run script on the API's origin. Vector art has to be an external URL.
- **Uploading is the `upload_files` capability**, not a content permission. Grant it to the custom
  role from step 5 and the client's editor can upload, describe and remove files; without it they
  cannot upload anything.
- **The API has a media library; the console does not yet.** `GET /api/files?q=&contentType=image/`
  lists and searches uploads, `PATCH /api/files/{id}` sets alt text and a caption, and
  `GET /api/public/files/{id}/meta` hands them to the frontend for a public file. Before deleting,
  `GET /api/files/{id}/usage` lists the entries that reference the file, and `DELETE /api/files/{id}`
  refuses with a 409 naming them until you pass `?force=true`. The grid and picker are barakoBrew's
  half of #113. Image variants are `?w=` on either download route; see `image-variants.md`.

If you use the S3 module instead of Postgres storage, the bytes are outside the database dump and
that bucket needs its own backup.

### Generating a client instead of hand-writing one

`/swagger/v1/swagger.json` carries the deployment's own delivery paths, not just the static routes:
the content types you created appear in it as real paths and schemas, per tenant. Turn Swagger on
(`Swagger__Enabled=true`) and fetch that document with the right `X-Tenant` to generate a typed client
for the client's frontend. It is invalidated when a type is created, when public delivery is toggled
and when a field's sensitivity changes, so it tracks the model.

There is no first-party generated client yet and no .NET client. Section 10.

### Previewing drafts

`POST /api/preview` mints a token bound to a tenant, a content type and a slug. It is authenticated,
and the caller also needs `read` on the entry, so minting a token is not a way around the permissions
that guard reading it normally. A slug read served under a valid `?preview=` token answers
`no-store` and can return an unpublished entry.

There is no button for this in the admin. A frontend that wants preview links calls that endpoint
from its own code. Deferred deliberately, and recorded here so nobody goes looking for a screen that
does not exist.

## 7. Deploy

[deploy-in-production.md](deploy-in-production.md) is the procedure. Short version: there is one
production compose file, `docker-compose.prod.yml`, it runs published images with Caddy in front, and
it refuses to start rather than booting on a placeholder.

```bash
git clone https://github.com/BaryoDev/barakoCMS.git
cd barakoCMS
cp .env.prod.example .env
$EDITOR .env
docker compose -f docker-compose.prod.yml config   # resolves every variable, exits non-zero on the first one missing
docker compose -f docker-compose.prod.yml up -d
```

The required variables are `DOMAIN_API`, `DOMAIN_ADMIN`, `ACME_EMAIL`, `FRONTEND_ORIGINS`,
`BARAKO_TAG`, `DB_PASSWORD`, `JWT_KEY` and `ADMIN_PASSWORD`. Both DNS records must resolve before you
start the stack, because Caddy requests certificates on first boot.

Delivery-specific things to get right at this point:

- **`FRONTEND_ORIGINS` is the client's site origin**, not yours. Add every origin that will call the
  delivery API from a browser, including a staging one.
- **Set `APP_BASE_URL` and `ALLOWED_HOSTS`** in `.env` (the keys are `App:BaseUrl` and
  `AllowedHosts`; both compose files pass them through). They are the pair that stops a caller
  choosing the origin of the links the API hands out, because the `Host` header is written by whoever
  sent the request. With neither set, the RSS feed answers 503 and the OAuth start endpoints fail,
  naming the setting. A Kubernetes `httpGet` probe sends the pod IP as `Host`, so a real hostname
  list makes probes 400 unless you add a `Host` header to the probe.
- **Set `FEEDS_SITE_URL` to the client's site**, not to the API. The feed prefers `Feeds:SiteUrl`
  over `App:BaseUrl` for the links in each item, and a reader following one should land on the
  client's page rather than on a JSON endpoint.
- **Pick a tag your machine can run.** The versioned image tags are `linux/amd64` only right now;
  `latest` carries both. On an arm64 host, pinning a version tag fails the pull. Check with
  `docker buildx imagetools inspect ghcr.io/baryodev/barako-cms:$BARAKO_TAG | grep Platform`. Tracked
  as #394.
- **Restore a backup before there is data worth keeping.** `db-backup` dumps nightly to its own
  volume, separate from Postgres's, and the script checks the exit code, proves the archive
  decompresses and enforces a minimum size before rotating.
  [backup-and-restore.md](backup-and-restore.md) is the procedure. A backup nobody has restored is
  not a backup.
- **Consider `Tenancy:DatabaseEnforcement`** on a multi-client deployment. Off, which is the default,
  a slipped application-layer filter has nothing underneath it. On, Postgres row-level security stops
  one tenant's session reading another's even when the filter is missed. It is not a settings change:
  it needs a connection role that is not a superuser, since a superuser bypasses row-level security
  entirely. [tenancy-at-the-database.md](tenancy-at-the-database.md) has the steps and the two limits.

## 8. Handover

### What you hand the client

- **The admin URL and their own account.** They sign in with the emailed code, or with a password a
  SuperAdmin set for them.
- **A role that is theirs.** Content permissions for their content types, no system capabilities.
- **The delivery API base URL and their content type names.** That is the whole contract their site
  depends on.
- **A content export.** `GET /api/portability/export` is one JSON bundle of their content types and
  content. Handing it over on day one is what makes "you own your content" checkable rather than a
  claim, and it is the same bundle that would seed the model somewhere else. You run it, not them:
  the endpoint gates on the seeded `Admin` and `SuperAdmin` role names, so a client-facing role
  cannot reach it. If they need it on demand, that is a standing job on your side rather than a
  button on theirs.
- **Where their data lives and what the backup story is.** Nightly Postgres dumps, no point-in-time
  recovery, and no measured RPO or RTO. Say the numbers you do not have.

### What stays yours

- **The `SuperAdmin` account.** It bypasses content permissions everywhere, holds every system
  capability, and is the only role that can create tenants, read the user list, reset another user's
  password and erase content. On a shared deployment it also reaches every other client, so it is not
  a credential to hand over.
- **Deployment configuration.** `JWT_KEY`, `Secrets:Key`, the database password, the domains, the
  email provider credentials. `EmailSettings` is a deployment-wide document, not a per-tenant one, so
  one mail provider serves every client on the instance. A client wanting mail from their own domain
  needs their own deployment.
- **`SystemSetting`** is deployment-wide too. Settings are not per tenant.
- **Upgrades.** Schema migrations run on start. Pin `BARAKO_TAG` and move it deliberately. Read
  [upgrading-to-4.0.md](upgrading-to-4.0.md) before moving to 4.0, which does not boot without its
  migration.

### The conversations to have before you sign anything

- **Erasure.** `Erasure:Mode` defaults to `Delete`: `DELETE /api/contents/{id}/erase` removes the
  item's events, its stream and its document in one transaction, and it is SuperAdmin only. `None`
  disables the path for a deployment that has decided its content holds no personal data, and needs
  an explicit acknowledgement to start. `CryptoShred` is not implemented and selecting it fails at
  startup rather than pretending. The audit trail is a second erasure surface, and it is unresolved:
  entries carry an actor username and the chain hashes each entry over its predecessor, so removing
  one breaks the tamper-evidence.
- **What a shared database means.** The honest trade-off is that a serious bug's blast radius is
  every tenant. Database-per-tenant is the same Marten API and is the escape hatch for anyone who
  needs isolation a bug cannot cross, but nothing here has been exercised that way.
- **Compliance.** No SOC 2, no ISO 27001, no third-party penetration test, no SLA.
  [compliance-posture.md](compliance-posture.md) states all of it on one page, which is faster than
  an unanswered email.

## 9. Choosing modules, and what it costs to change later

Modules are optional NuGet packages layered on the core. Two ways to run them.

**The suite image** (`ghcr.io/baryodev/barako-cms`) ships every module and each stays off or on a
mock until its keys are set. Turning one on is adding environment variables and restarting. This is
the default and the cheap answer: an empty but valid `.env` boots a working CMS you can grow into.

**The lean core** (`ghcr.io/baryodev/barako-cms-decaf`) ships nothing, and you reference the modules
you want from your own host. The package reference plus a restart is the install:

```sh
dotnet add package BarakoCMS.Files
dotnet add package BarakoCMS.Email.Resend
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);
var app = builder.Build();
app.UseBarakoCMS();
await app.RunBarakoModuleSeedersAsync();
```

`AddBarakoCMS` finds every module in the application's dependency context, and
`BarakoCMS:Modules:Enabled` decides which of them run (`BarakoCMS__Modules__Enabled=Files,Email.Resend`).
Unset, every referenced module runs and the API logs one warning saying so; an empty string is core
only. A host that wants to name its modules by hand puts `modules.Add(new BarakoCMS.Files.FilesModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md`.

Adding a module later is cheap in both shapes. A module's configuration is its own `Modules:{Name}`
section, its schema is created on start, and its seed is idempotent and runs every boot. Removing one
is the harder direction, because its documents stay in the database and nothing reads them any more.

The expensive decisions are the ones that are not modules at all:

| Decision | Reversible? |
| --- | --- |
| `eventSourced` on a content type | No, and it is recorded against the name, so recreating the type does not clear it |
| Event store tenancy style | No. Changing it on an existing store is not a live migration |
| A field name, field type or reference target, once entries exist | No update endpoint |
| Public delivery on a type | Yes, `PUT /api/content-types/{name}/public-delivery` |
| A field's sensitivity | Yes, but lowering it is a retroactive disclosure and requires acknowledgement |
| Which modules are installed | Yes, adding is cheap |

## 10. What is not solved yet

A delivery document that overstates is worse than none. These are the gaps as the code stands, not as
the roadmap describes it.

### Anyone who can reach the API can create an account on it

`POST /api/auth/register` is anonymous, rate limited to five an hour per address, and there is no
setting that turns it off. `Auth:RequireEmailVerification` gates whether the new account has to
confirm its address; it does not gate whether the account gets made. The account lands with the
`User` role, so it reads nothing an anonymous caller could not already read, but it is a row in the
client's user list that the client did not put there.

If the delivery API is public, say so before handover rather than after the client finds accounts
they did not create. Putting `/api/auth/register` behind the reverse proxy is the answer available
today.

### Two administrative surfaces reach past the tenant

Both are reachable by the seeded `Admin` role, which is why section 5 says not to give it to a
client's staff.

**`GET /api/audit` is not scoped to the caller's tenant.** `?tenant=` is a filter the caller chooses,
not a boundary the server applies. A caller holding `view_audit_log`, which `Admin` holds by default,
reads every tenant's audit events on a shared deployment.

**`POST /api/users/{userId}/roles` writes a global role.** `User.RoleIds` is global, and effective
roles are the union of that and the caller's membership roles in the current tenant, so a role
assigned there applies in every tenant. It is gated on `manage_user_membership`, which `Admin` holds
by default, and unlike `POST /api/tenants/members` it does not refuse the SuperAdmin role id. The
system role ids are deterministic and in the source.

The tenant-scoped way to grant a role is `POST /api/tenants/members` and
`PUT /api/tenants/members/{userId}`, which write only `Membership.RoleIds` and refuse SuperAdmin.
Until those two surfaces are scoped, a client-facing role must carry no system capabilities at all.

### Things a client site usually wants that do not exist

- **No SEO fields** (#111). Title, description and canonical URL are fields you model yourself on
  every content type.
- **No URL redirects** (#112). Rebuilding a site breaks its old links, and nothing in barakoCMS
  catches them.
- **No media library the client can use** (#113). The Files module stores and serves bytes and has no
  admin screen at all: no browsing, no picking, no image variants (#100). Uploading is gated on the
  seeded `Admin` and `SuperAdmin` role names, so a client editor cannot add an image without a role
  that reaches further than their content.
- **No form submissions module** (#110). A contact form has nowhere to go.
- **No content-type blueprints** (#109). A new client starts from an empty schema or from a JSON file
  you keep yourself. The Portability bundle in step 4 is the workaround, not the feature.
- **No starter frontend templates** (#188). `examples/blog-starter` is a worked example, not a
  template you clone.
- **No localization** (#98). One entry is one language.

### Things about the delivery path itself

- **No pinned client generator and no .NET client** (#183, #186). The OpenAPI document is generated
  and correct; what to run over it is not decided, and nothing notices when a generated client stops
  matching the API (#187).
- **No API versioning on the public delivery endpoints**, by decision (#107, D14). A breaking change
  to the delivery contract lands only in a major (a security fix excepted) and is announced at
  least one minor ahead;
  [delivery-api.md](delivery-api.md) has the rule. It still reaches every client site at once when
  the major ships.
- **No webhooks and no realtime** (#95, #96). A frontend that caches has to poll or rebuild on a
  schedule.
- **No preview screen in the admin.** `POST /api/preview` exists and there is deliberately no button
  for it, so preview links are minted by the frontend's own code.
  [delivery-api.md](delivery-api.md) records that as deferred rather than overlooked.

### Things about operating it

- **The TLS path is not covered by CI** (#308). CI resolves every compose file and asserts the
  production one builds nothing, and the same images run on the playground, but certificate issuance
  on a real VM is verified by hand.
- **Versioned image tags are amd64 only** (#394).
- **No CLI** (#169, #345). Everything in this document is barakoBrew or curl. There is no
  reviewable file that configures an instance, which is what would make step 3 through step 5
  repeatable per client instead of retyped.
- **No per-tenant email settings.** One provider for the deployment.
- **Custom domains and branding are database writes**, as section 3 says.
- **Per-field sensitivity can be set when a content type is created but not edited in barakoBrew.**
  Changing it afterwards is `PUT /api/content-types/{name}/fields/{field}/sensitivity`, called
  directly.

## Related documents

- [multi-tenancy.md](multi-tenancy.md), what is per tenant and what is shared
- [access-control.md](access-control.md), the three permission layers and the capability model
- [delivery-api.md](delivery-api.md), the anonymous read surface in full
- [deploy-in-production.md](deploy-in-production.md), the production compose procedure
- [backup-and-restore.md](backup-and-restore.md), and what is not covered
- [configuring-email.md](configuring-email.md), which is a prerequisite for inviting anybody
- [compliance-posture.md](compliance-posture.md), the answers a client's reviewer asks for
- [tenancy-at-the-database.md](tenancy-at-the-database.md), row-level security and its constraints
- [`quickstart/README.md`](../quickstart/README.md), the local instance
- [`examples/blog-starter`](../examples/blog-starter/), the worked frontend
- [MODULES.md](../MODULES.md), the module contract
