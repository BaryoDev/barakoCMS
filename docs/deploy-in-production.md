# Deploying barakoCMS on a VM

One `docker compose up -d` on a clean machine, using the images the release workflow publishes.
Nothing is compiled on the target host.

Not running a VM? App Service, Fargate and Cloud Run are covered in
[deploy-on-a-managed-platform.md](deploy-on-a-managed-platform.md).

There is one production compose file, `docker-compose.prod.yml`. The other compose files in this
repository are for other jobs and say so in their headers:

| File | What it is for |
| --- | --- |
| `docker-compose.prod.yml` | Production. Published images, Caddy and TLS, required secrets, nightly backup. |
| `docker-compose.yml` | Local development. Builds from source, dev defaults. |
| `docker-compose.hub.yml` | Local try-out of the published images. No TLS, dev defaults. |
| `quickstart/docker-compose.yml` | Quickstart, and the worked example of every module's settings. Bring your own proxy. |

## What you need

- A machine with Docker and the Compose plugin.
- A DNS A record for the API pointing at it. It must resolve before you start the stack, because
  Caddy requests a certificate on first boot and Let's Encrypt validates over HTTP.
- Ports 80 and 443 reachable from the internet.
- A checkout of this repository on the machine. The compose file mounts `Caddyfile` and
  `scripts/backup-cron.sh` from it. What you no longer need is a .NET SDK or Node.

## Deploy

```bash
git clone https://github.com/BaryoDev/barakoCMS.git
cd barakoCMS
cp .env.prod.example .env
$EDITOR .env
docker compose -f docker-compose.prod.yml up -d
```

`.env.prod.example` lists every variable with the command that generates it. The required ones have
no default, so the stack refuses to start rather than booting on a placeholder:

- `DOMAIN_API`, `ACME_EMAIL`
- `FRONTEND_ORIGINS`, the origins of the console and of the site you are about to build
- `BARAKO_TAG`, a published version, not `latest`
- `DB_PASSWORD`, `JWT_KEY` (32 characters or more), `ADMIN_PASSWORD`

A missing one fails immediately and names itself:

```console
$ docker compose -f docker-compose.prod.yml config
error while interpolating services.app.environment.[]: required variable JWT_KEY is missing a value: set JWT_KEY in .env to at least 32 random characters
$ echo $?
1
```

`config` is worth running before `up` for exactly this: it resolves every variable and exits
non-zero on the first one you have not set.

## Check it came up

```bash
docker compose -f docker-compose.prod.yml ps
curl -o /dev/null -w '%{http_code}\n' https://$DOMAIN_API/api/schemas   # 401, the endpoint is role gated
```

A 401 means routing and TLS work and the endpoint still checks its role. A 404 means Caddy is not
routing. A 200 would mean the role check is gone.

Sign in with `ADMIN_USER` and `ADMIN_PASSWORD`, from Swagger at `https://$DOMAIN_API/swagger` if you
set `Swagger__Enabled=true` on the `app` service, or from the console. The console is
[barakoBrew](https://github.com/BaryoDev/barakoBrew): deploy it from its own repository, point it at
`https://$DOMAIN_API`, and put its origin in `FRONTEND_ORIGINS`.

## Watching workflows

Workflow actions run in the background, so a runner that has stopped changes nothing a request can
see. The API publishes when the runner last completed a pass, how many runs are due and how old the
oldest is, and how each action attempt ended, on `/metrics`. Set `Metrics__ScrapeKey` on the `app`
service to switch the endpoint on, and see "Watching the runner" in `docs/workflow-runs.md` for the
metric names, what each alert fires on, and the forms for more than one node. On a single VM, the
four to start with:

```
time() - barakocms_workflow_runner_last_pass_timestamp_seconds > 600
absent(barakocms_workflow_runner_last_pass_timestamp_seconds)
barakocms_workflow_oldest_due_run_age_seconds > 900
  and on (instance) (time() - barakocms_workflow_backlog_measured_timestamp_seconds < 120)
absent(barakocms_workflow_backlog_measured_timestamp_seconds)
```

In order: the runner has not completed a pass in ten minutes, it has never completed one, work has
been due for fifteen minutes on a measurement that is fresh, and the backlog has never been
measured. The first says nothing on a runner that never completed a pass, and the third says
nothing while the backlog was never measured, which is why each has its `absent` after it. The backlog is
measured every 30 seconds by default (`Workflows__BacklogIntervalSeconds`, 0 switches it off, and
the last two alerts go with it).

## Pointing a frontend at it

Your own frontend calls the delivery API directly:

```http
GET https://api.example.com/api/public/{contentType}
```

Two switches decide whether a content type is served there. The type has to be opted in, and each
field has to be marked Public. Both are set on the content type, from barakoBrew or
`POST /api/content-types`. A type that is not
opted in returns 404, and a field that is not Public is absent from the response rather than empty.

The browser origin calling that API must be in `FRONTEND_ORIGINS`. If it is not, the request fails
CORS in the browser and looks like the API is down.
`FRONTEND_ORIGINS` becomes `CORS__AllowedOrigins` on the `app` service, and
`BarakoCMS.Tests/CorsTests.cs` pins what a listed and an unlisted origin get back from a preflight.

## A client's own domain, with no config edit

A tenant's domains are data (`PUT /api/tenants/{handle}` with `domains`, see
[delivering-a-client-project.md](delivering-a-client-project.md#custom-domains)). Two opt-in pieces
let a domain added there work in a browser without touching `FRONTEND_ORIGINS` or a `Caddyfile`.

**CORS.** Set `CORS__AllowTenantDomains=true` on the `app` service. The API then also allows an
origin that is exactly `https://` plus a domain an active tenant holds, or its `www.` form. These
are refused: `http`, any port, a path, user info, a wildcard, an IP literal, `null`, and any other
subdomain of a registered domain. A tenant domain origin never gets
`Access-Control-Allow-Credentials`, so a page there cannot use a visitor's refresh cookie; an origin
that needs credentials goes in `FRONTEND_ORIGINS`, and a listed origin keeps them. With the setting
on, every response that gets past the rate limiter and tenant resolution carries `Vary: Origin`,
whether or not the request had an `Origin`, so a shared cache does not hand a response fetched by a
renderer to a browser on a tenant domain. A 429, or a Multi-mode 404 for an unknown tenant, does not
carry it. Any value but `true` leaves the setting off.

Only a platform administrator (`manage_tenants`) can write a tenant's domains, so this does not let
a tenant administrator add an origin. The domain map is cached: a change applies on the next request
on the instance that saved it and within `Multitenancy:CacheDuration` (five minutes by default) on
the others. If the map cannot be read, only `FRONTEND_ORIGINS` is allowed.

**TLS.** `GET /api/tenants/tls-ask?domain={host}` answers 200 for a domain an active tenant holds
(or its `www.` form) and 404 for anything else, naming no tenant. It is Caddy's on-demand TLS `ask`
endpoint. It is anonymous and answers in Multi mode without a tenant.

It is limited by the name asked about, not by the caller, because Caddy asks every question from its
own address. The `tls-ask` policy (fixed) spreads names over 4096 buckets by a hash seeded per
process and allows 60 questions a minute per bucket, and the route is left out of the global per-IP
limit. A flood of made-up server names then lands in thousands of buckets and leaves a real name's
bucket nearly empty, and the limiter never holds more than 4096 partitions. What remains: asking
about one real name more than 60 times a minute, or about names found by probing to share its
bucket, holds that name's answers at 429 (which Caddy reads as no) while it lasts.

A site proxy that issues certificates on demand, for barakoPress serving the client domains (the
shipped `Caddyfile` here serves only `{$DOMAIN_API}` and stays as it is):

```caddyfile
{
    email {$ACME_EMAIL}
    on_demand_tls {
        ask http://app:8080/api/tenants/tls-ask
    }
}

https:// {
    tls {
        on_demand
    }
    reverse_proxy press:3000
}
```

`TenantDomainCorsTests`, `TenantDomainCorsPolicyProviderTests`, `TenantDomainLookupTests` and
`TlsAskTests` cover both.

## Putting a shared cache or CDN in front of it

The shipped production `Caddyfile` is a plain `reverse_proxy` with no cache handler, so a stack
running only that file reaches Postgres on every public read. This section describes what you put in
front of it, not something the stack does for you.

The delivery API (`docs/delivery-api.md`) marks its responses `Cache-Control: public, max-age=60`,
which is an invitation to put a CDN in front of it. Whether that is safe depends on how tenants are
routed (`docs/multi-tenancy.md`), because the response is cacheable per tenant, not globally.

Each read also carries a weak ETag for revalidation and `Surrogate-Key` and `Cache-Tag` headers
whose tags all start with the tenant, so a CDN that purges by tag can drop exactly what an entry
appears in. Most CDNs strip both tag headers before the browser. The tag headers stay under 1024
bytes each, inside a default nginx `proxy_buffer_size`. See "Cache classes, validators and tags" in
`docs/delivery-api.md`.

This section is about that cacheable majority. `GET /api/public/events`, the change stream, is not
part of it: it sends `no-store` and must never be cached, for reasons that have nothing to do with
tenancy. See the "Change events" section of `docs/delivery-api.md` for what a proxy in front of it
has to do instead.

**Safe on nearly every CDN's default behaviour, and still worth confirming rather than assuming:**

- a single-tenant deployment, where `X-Tenant` is never sent and every request resolves to the same
  tenant, so there is no second tenant a cache entry could be confused with, and
- a multi-tenant deployment with one hostname per tenant (a subdomain or a custom domain). Keying the
  cache on the full URL, Host included, is near-universal default behaviour, not a law: it is still a
  property of that CDN's own configuration. Confirm it is not running a rule that treats every host on
  the origin as one cache key (most often a wildcard-TLS or single-origin setting, not one aimed at
  caching, but it has the same effect here), which would undo the guarantee this relies on.

**Not safe without configuring the CDN, and `Vary: X-Tenant` alone does not make it safe:**

- a deployment routed by the `X-Tenant` header, where more than one tenant is reachable through the
  same hostname and path, and
- path-based routing where the front end resolves the tenant from the URL handle and forwards it as
  `X-Tenant`, if the path the CDN sees no longer carries that handle (the tenant-distinguishing part
  of the request is only in the header by the time a shared cache looks at it).

On both of those, this API sends `Vary: X-Tenant` so a conforming cache knows the response depends on
it, but `Vary` is a request to the cache, not a guarantee. A CDN that does not honour it, or honours
`Vary` only for a fixed set of headers that does not include `X-Tenant`, or strips the header before
the cache lookup runs, still serves one tenant's response to another. Before putting a shared cache
in front of a header- or path-routed deployment, confirm and configure, for the specific CDN:

- it keys its cache on the `X-Tenant` request header, not on the URL alone (this is usually a
  separate "cache key" setting from "respect origin `Vary`", and the header often has to be named
  explicitly),
- it does not strip, rename or coalesce `X-Tenant` before the cache lookup, and
- `X-Tenant` reaches the origin unchanged on a cache miss, so the value the cache keyed on is the one
  this API actually resolved against.

If the CDN cannot be made to do all three, do not put it in front of that deployment. `max-age=60` is
short enough that skipping the shared cache and letting every request reach the origin is the safer
default.

## Modules

Every module ships in the `barako-cms` image and stays off or mocked until you configure it. To turn
one on, add its variables to the `app` service's `environment:` block.
`quickstart/docker-compose.yml` lists all of them with a note on what each does.

For the lean core instead of the suite, swap `ghcr.io/baryodev/barako-cms` for
`ghcr.io/baryodev/barako-cms-decaf` and reference the module packages you want from your own host.

## Client IPs behind Caddy

Rate limiting and the audit log use the client IP that Caddy puts in `X-Forwarded-For`. The app
believes that header only from Caddy's own address. Caddy and the app share a `proxy` network with a
fixed subnet, Caddy has a fixed address on it, and the app trusts that one address:

| Variable | Default | What it sets |
| --- | --- | --- |
| `PROXY_SUBNET` | `10.87.51.0/28` | the `proxy` network's subnet |
| `PROXY_ADDRESS` | `10.87.51.2` | Caddy's address on it, and the one address the app trusts |
| `TRUSTED_PROXY_NETWORK` | empty | an extra trusted range in CIDR notation, if you need one |

If `10.87.51.0/28` is already used on your host, by another Docker network or by a LAN or VPN route,
set `PROXY_SUBNET` to a free range and `PROXY_ADDRESS` to an address inside it. An overlap with
another Docker network fails `up` with `Pool overlaps with other one on this address space`. An
overlap with a host route does not fail anything, it just stops the containers reaching that range,
so check `ip route` before you pick.

`ForwardedHeadersTests` reads these defaults out of the compose file and checks that a request from
Caddy's address has its `X-Forwarded-For` honoured and a request from another container does not.

## Postgres on a small server

Postgres ships with settings for a machine of unknown size: `shared_buffers` 128MB,
`random_page_cost` 4 (a spinning disk), `jit` on. `docker-compose.prod.yml` and the quickstart set
them for a 2 GB VM that also runs the API, and every value is a variable you can set in `.env`:

| Variable | Default (2 GB) | 4 GB | 8 GB | Postgres stock |
| --- | --- | --- | --- | --- |
| `PG_SHARED_BUFFERS` | `512MB` | `1GB` | `2GB` | `128MB` |
| `PG_EFFECTIVE_CACHE_SIZE` | `1GB` | `2GB` | `5GB` | `4GB` |
| `PG_WORK_MEM` | `4MB` | `8MB` | `16MB` | `4MB` |
| `PG_MAX_CONNECTIONS` | `100` | `100` | `100` | `100` |
| `PG_RANDOM_PAGE_COST` | `1.1` | `1.1` | `1.1` | `4` |
| `PG_JIT` | `off` | `off` | `off` | `on` |

How the numbers are picked:

- `shared_buffers` is about 25% of the machine's RAM.
- `effective_cache_size` is a planner hint, not an allocation. It is about 50% of RAM on 2 GB, where
  the API and console take roughly 400 MB, and closer to 60 to 75% on the larger sizes.
- `work_mem` is per sort or hash, per connection, so `max_connections` times `work_mem` plus
  `shared_buffers` has to fit in RAM with room to spare. On 2 GB that is 100 x 4MB + 512MB, about
  900 MB at the worst case. `max_connections` stays at the stock 100 so the API's connection pool
  keeps fitting; lowering it can turn a busy minute into `too many clients` errors.
- `random_page_cost` 1.1 assumes an SSD, which every current cloud VM disk is. On a spinning disk
  set it back to `4`.
- `jit` off, because compiling a plan costs more than it saves on the small queries a CMS runs.

None of these allocate at start except `shared_buffers`, and Postgres maps that lazily, so a
container memory limit below it does not stop Postgres starting. It does let Postgres be killed
once the buffers fill, so if you cap the container's memory, set `PG_SHARED_BUFFERS` to a quarter of
that cap. On a server larger than 8 GB, or with other databases on it, size from the table rather
than the defaults.

These are starting values. The measured numbers for them are recorded in #798.

### Finding slow queries

Both files load `pg_stat_statements`, and a one-shot `postgres-extensions` service creates the
extension each time the stack starts (a no-op after the first). It runs on an existing volume too,
which a script in `docker-entrypoint-initdb.d` would not. It exits once done, so `docker compose ps`
shows it only with `-a`. To see the slowest queries:

```bash
docker compose -f docker-compose.prod.yml exec postgres psql -U postgres -d barako_cms -c \
  "SELECT round(mean_exec_time::numeric, 1) AS mean_ms, calls, left(query, 80) AS query
   FROM pg_stat_statements ORDER BY mean_exec_time DESC LIMIT 10;"
```

Use your `DB_USER` and `DB_NAME` if you changed them. `SELECT pg_stat_statements_reset();` starts
the counts again, which is worth doing before a measurement.

### Upgrading to these settings

The next `docker compose -f docker-compose.prod.yml up -d` recreates the `postgres` container with
the new settings, which is a restart of a few seconds. The data volume is not touched. To keep the
stock values, set the variables to the "Postgres stock" column in `.env`.

### Measuring delivery

`scripts/delivery-load.py` sends a fixed rate of requests for a fixed time to the list, one entry by
slug and `GET /api/public/site`, and prints p50, p95 and errors for each. It needs only `python3`:

```bash
python3 scripts/delivery-load.py --base-url https://$DOMAIN_API --type post --slug hello-world \
  --rate 1.5 --duration 60
docker stats --no-stream   # in a second shell while it runs, and again at rest
```

The API allows 100 requests a minute per client address by default (`RateLimiting:Global`, see "Rate
limits" below), so from one address a rate above about 1.6 a second measures the limiter instead: requests queue, then come
back as 429, and the script says so. The default rate of 1.5 stays under it.

`--type` must be publicly deliverable with a published entry at `--slug`, and the site must have a
published `site` entry; the script checks all three answer 200 before it starts. It exits 1 if any
request failed. Run it from the VM itself to measure the stack, or from outside to include the
network and Caddy.

## Rate limits

Every limit is a setting. The defaults are the limits earlier releases hard coded, so an upgrade
changes nothing until you set one. Each has `PermitLimit` (requests), `WindowSeconds` and
`QueueLimit` (requests that wait for the next window instead of getting 429):

| Section | Default | Applies to |
| --- | --- | --- |
| `RateLimiting:Global` | 100 in 60 seconds, queue 10 | every request, per client IP |
| `RateLimiting:Auth` | 5 in 900 seconds, queue 0 | login, refresh, OTP and MFA, per client IP |
| `RateLimiting:Batch` | 20 in 60 seconds, queue 0 | anonymous telemetry batches, per client IP |
| `RateLimiting:Registration` | 5 in 3600 seconds, queue 0 | registration and its verification, per client IP |
| `RateLimiting:SiteShare` | 10 in 60 seconds, queue 0 | anonymous share link redemption, per tenant and visitor |

As environment variables on the `app` service, the colon becomes a double underscore:

```yaml
- RateLimiting__Global__PermitLimit=600
- RateLimiting__Global__WindowSeconds=60
```

A zero or negative `PermitLimit` or `WindowSeconds`, a negative `QueueLimit`, or a value that is not
a whole number stops the host at startup with the setting named. There is no way to turn a limit off.
Setting `Auth` or `Registration` looser than its default is allowed and logs a warning at startup,
since those two exist to slow down guessing.

The client IP is the one described in "Client IPs behind Caddy" above. Get that right first, or every
client shares Caddy's bucket.

### A renderer serving many sites

One barakoPress container renders every site it serves, so all of their reads come from one IP and
share one global bucket. Give it a key instead:

| Setting | Default | What it does |
| --- | --- | --- |
| `RateLimiting__Renderer__Key` | unset | a secret of at least 32 characters. Unset means no renderer partition |
| `RateLimiting__Renderer__PermitLimit` | `1000` | requests in the renderer's own bucket |
| `RateLimiting__Renderer__WindowSeconds` | `60` | the window for that bucket |
| `RateLimiting__Renderer__QueueLimit` | `10` | requests that wait for the next window |

A request with the header `X-Barako-Renderer-Key` equal to the key is counted in one renderer bucket
instead of its IP's bucket. A missing or wrong key is an ordinary request, counted against its IP. The
key moves a request to the renderer bucket of the global limit, and such a request is not counted by
the delivery limit below when one is set. `Auth`, `Batch` and `Registration` stay per IP with or
without it.
The key is compared in constant time and never logged. Keep it in `.env` like the JWT key, and set the
same value in barakoPress (`CMS_RENDERER_KEY`).

Share link redemption (`POST /api/public/site/share-links/redeem`, and `.../open` for a link to one
entry or page, which shares the same budget) is limited per tenant and per
visitor by `RateLimiting__SiteShare__*` (10 a minute by default). barakoPress redeems server side,
so every visitor arrives from its IP. A redeem carrying the renderer key may also send
`X-Barako-Visitor-IP` with the visitor's address, and that address is then the visitor. The header
must be one IPv4 or IPv6 literal; anything else, or the header without a matching key, is ignored
and the socket IP is used. The tenant in that bucket is what the request names, `X-Tenant` or else
the host, because the limiter runs before the tenant is resolved. A caller sending many hosts that
resolve to one tenant gets a bucket per host, so for that caller the ceiling is the global limit.

A renderer behind a proxy should use the key rather than rely on `X-Forwarded-For`. Trusting another
proxy's forwarded header widens who can choose the client IP; the key is a secret only the renderer
holds.

`RateLimitSetupTests` covers the defaults, the startup failures, the renderer bucket and a wrong key.

### Limits that are off until you set them

Three more limits have no default. Unset, nothing changes: the routes they cover stay on the limits
in the table above. Each is on once its `PermitLimit` is set. `WindowSeconds` defaults to `60` and
`QueueLimit` to `0`. Setting `WindowSeconds` or `QueueLimit` without `PermitLimit` stops the host at
startup, since it would read as a limit that is on while it is off.

| Section | Counts | Applies to |
| --- | --- | --- |
| `RateLimiting:Delivery` | per client IP | `GET /api/public/{type}`, its `/search`, `/{slug}` and `/feed.xml`, and `/api/public/sitemap.xml` |
| `RateLimiting:ApiKey` | per API key | every request an API key authenticated, on any route |
| `RateLimiting:Policies:{name}` | per client IP, user or API key | the routes that name the policy |

```yaml
- RateLimiting__Delivery__PermitLimit=300
- RateLimiting__ApiKey__PermitLimit=1000
- RateLimiting__ApiKey__WindowSeconds=3600
```

All of these are counted on top of the global limit, never instead of it. A delivery limit above
the global one (100 a minute per client IP by default) therefore never refuses anything until
`RateLimiting:Global` is raised too. A request carrying the renderer key is not counted by the
delivery limit. It stays in the renderer bucket.

The API key quota is counted against the key the API verified, not against what the request
presents, so it follows the key across addresses and a made-up key opens no bucket: that request is
not authenticated and is counted against its IP like any other. A signed-in session is not on the
quota. One number applies to every key; a key cannot be given its own.

#### Named policies

A policy is a name, numbers and what it counts against:

```yaml
- RateLimiting__Policies__lookup__PermitLimit=20
- RateLimiting__Policies__lookup__WindowSeconds=60
- RateLimiting__Policies__lookup__Partition=Ip
```

A route in a module or in the host names it in code, `RequireRateLimiting("lookup")`, so
its numbers can change without a build. `Partition` is `Ip` (the default), `User` or `ApiKey`. With
`User` or `ApiKey` a caller who has no such id, an anonymous one for instance, is counted in one
bucket shared by every such caller.

A name is up to 64 letters, digits, dashes, underscores and dots, and must be spelled on the route
exactly as in configuration, case included. A name with a dash or a dot cannot be exported as an
environment variable from a POSIX shell, so keep to letters, digits and underscores if that is how
you set it.

The core's own names are reserved: `auth`, `telemetry`, `registration`, `site-share`, `logout`,
`tls-ask`, `delivery` and `email-preview` (30 email template previews a minute per signed-in user,
fixed). They cannot be defined under `Policies`; the sections above set
them. A host or a module that registers its own policy named `delivery` in code stops at startup
with a message saying to rename it. A name under `Policies` that a module already registers in code
(`forms`, with the Forms module on) stops the host the same way, naming the setting. Any route
mapped by the time the API is set up that names a policy nobody defined stops the host at startup
with the route and the policy named.

#### What these counters are

Every limit on this page is counted in the memory of one API process. With two instances behind a
load balancer a client can spend each limit twice, and a restart starts every count again. A bucket
is dropped about ten seconds after its window has passed with no request, so the number held at once
is the number of distinct clients, users or keys seen in the last window, not all that were ever
seen. There is no fixed cap on that number.

`NamedRateLimitPolicyTests` covers the defaults, the settings that stop the host, a configured
policy, a forged `X-Forwarded-For`, the delivery limit and the key quota. `RateLimitStartupCheckTests`
covers a route naming a missing policy and the reserved names, on a real host.

## Content type field limit

A content type may hold at most 200 fields. Creating one with more, or adding a field that would take
it past the limit, is refused with 400. Raise or lower it with `ContentTypes:MaxFields`
(`ContentTypes__MaxFields` in compose); a value below 1 is treated as 1. A type already stored with
more fields than the limit keeps working, and only adding fields to it is refused.
`FieldCountCapTests` covers this.

## Upgrading

```bash
docker compose -f docker-compose.prod.yml pull
docker compose -f docker-compose.prod.yml run --rm --no-deps app db-migrate --status
```

Change `BARAKO_TAG` first. `--status` only reads, so it is safe while the old container serves. It
exits 0 when this database has had every SQL file the new build ships, and then the upgrade is:

```bash
docker compose -f docker-compose.prod.yml run --rm --no-deps app db-assert
docker compose -f docker-compose.prod.yml up -d
```

When `--status` lists a migration as `pending` or `not-needed` and exits 1, stop the API before
migrating:

```bash
docker compose -f docker-compose.prod.yml stop app
docker compose -f docker-compose.prod.yml run --rm --no-deps app db-migrate
docker compose -f docker-compose.prod.yml run --rm --no-deps app db-assert
docker compose -f docker-compose.prod.yml up -d
```

`db-migrate` (4.6.0 and later) applies the SQL files the release ships that this database has not
had, and records each one. Do not run it against a serving API, apart from the few files
[migrations.md](migrations.md) names as safe under the old build: a file waits on the locks the API
holds, and an index built `CONCURRENTLY` does not finish while the API has a transaction open.
[migrations.md](migrations.md) covers that, what the first run does on a database that was migrated
by hand, and how to stop a run. `db-assert` exits 0 when the database already holds every object
the new build declares, and non-zero listing what is outstanding. The API is down from the `stop`
to the `up`, so read what `--status` listed before you begin.

Those steps are not optional on an existing database. A new table is created on start, but an
existing table is never altered: production runs `AutoCreate.CreateOnly`. So a release carrying a
change to a table you already have does not migrate it and does not skip it either, it throws on
start and the container restarts forever, with the previous one already gone. Releases that need
this ship the SQL in `migrations/<version>/`, and `db-migrate` is what applies it. An image older
than 4.6.0 has no `db-migrate`: apply the files with `psql` as each file's header says.

Read [upgrading-to-4.0.md](upgrading-to-4.0.md) before moving to 4.0, which does not boot without
its migration.

### Upgrading past 4.0.2: the trusted proxy

Before 4.0.2 this file trusted `X-Forwarded-For` from anything in `172.16.0.0/12`, which is every
container on a default Docker bridge. It now trusts only Caddy's address (see "Client IPs behind
Caddy" above). The next `up -d` creates the `proxy` network and recreates `caddy` and `app` on it.
Postgres and its volume are not touched.

- **You never set `TRUSTED_PROXY_NETWORK`.** Nothing to do. Caddy moves to `PROXY_ADDRESS` and the
  app trusts that address, so client IPs keep working. If `up` fails with a pool overlap, set
  `PROXY_SUBNET` and `PROXY_ADDRESS` as described above.
- **You set `TRUSTED_PROXY_NETWORK`.** It is still applied, so everything you trusted before is still
  trusted, and Caddy's new address is trusted on top. To close the gap this release is about, remove
  it from `.env` unless it names something other than Caddy that you do need.

## Backups

`db-backup` dumps nightly to the `postgres_prod_backups` volume, which is its own and not Postgres's,
so losing the database volume does not lose the backups. The script checks pg_dump's exit code,
proves the archive decompresses, and enforces a minimum size before rotating.

Restoring is [backup-and-restore.md](backup-and-restore.md). A backup nobody has restored is not a
backup, so do it once before you have data worth keeping.

## Known rough edges

- **Pick a tag your machine can run.** The `3.21.0` version tags of both images are
  `linux/amd64` only. Every tag from `4.0.0` on, and `:latest` of both images, carries amd64 and
  arm64. On an arm64 host (Ampere, Graviton, an Apple laptop) pinning `BARAKO_TAG=3.21.0`
  fails the pull with `no matching manifest for linux/arm64/v8`. Check before you pin an older tag:

  ```bash
  bash scripts/check-image-platforms.sh ghcr.io/baryodev/barako-cms:$BARAKO_TAG
  bash scripts/check-image-platforms.sh ghcr.io/baryodev/barako-cms-decaf:$BARAKO_TAG
  ```

  Run the line for the image you actually pull; the two are published separately and one can
  carry both architectures while the other does not. That is the same check the release workflow runs on every tag it publishes, and CI proves it
  fails on `3.21.0`. Tracked as #394.

  The console image has the same problem on its old name: `barako-admin:3.21.0` is amd64 only. It
  was the last tag built in this repository before the console split out, and this repository has
  no way to fix it. The console is published from
  [BaryoDev/barakoBrew](https://github.com/BaryoDev/barakoBrew) now, as
  `ghcr.io/baryodev/barako-brew` from barakoBrew 1.2.0 (also pushed as `barako-admin` until 2.0.0),
  and its publish workflow runs the same platform check. Do not pin the console to `3.21.0`.

  Since `4.0.0` the release workflow builds both platforms for `barako-cms` and `barako-cms-decaf`
  and refuses to publish either image's versioned tag or `:latest` unless the pushed manifest shows
  both. That gate covers this repository's own images; barakoBrew runs its own on the console image.

- **The first nightly-backup container logs a failure** (#395). `db-backup` starts as soon as Postgres is
  healthy and takes a proof backup immediately, which on a fresh stack races the API's schema
  creation. The dump comes out empty, the size guard rejects it, and you get
  `BACKUP FAILED: archive is only 368 bytes` once. That guard is the point, and nothing was written.
  Backups from the schedule are fine. Confirm with
  `docker compose -f docker-compose.prod.yml restart db-backup` after the API is up.

- **Seed data.** A fresh instance seeds demo roles, an `AttendanceRecord` content type and three
  demo entries, used to demonstrate field sensitivity. Delete what you do not want before the site
  is public. Tracked as #283.

- **TLS on a real VM is not covered by CI.** CI resolves every compose file and asserts the
  production one builds nothing, but the certificate issuance path is verified by hand.
  Tracked as #308.

