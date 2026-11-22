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
| `docker-compose.hub.yml` | Local try-out of the published images. No TLS, localhost only. Needs `JWT_SECRET` and `DB_PASSWORD` set (`openssl rand -base64 48`). |
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
- `DB_PASSWORD`, `DB_APP_PASSWORD`, `JWT_KEY` (32 characters or more), `ADMIN_PASSWORD`

The API refuses to start outside Development on any example secret this repository prints: the
compose dev key, the keys in the quickstart and the docs, and the database passwords `postgres`,
`change-me-to-a-strong-password` and the like. The message names the setting, never the value.

Every service in `docker-compose.prod.yml` runs with a read-only root file system, all Linux
capabilities dropped (Caddy adds back the one that binds ports 80 and 443, Postgres the five its
entrypoint needs) and `no-new-privileges`. The API runs as a non-root user. Each service has a memory
and a process cap, set from `.env` with generous defaults, listed at the end of `.env.prod.example`.
Raise `API_MEM_LIMIT` before a large import. `scripts/check-compose-hardening.sh` fails CI when a
service in any shipped compose file loses one of these.

Compose cannot refuse a limit set to `0` or `-1`, and either one removes the cap. Check your own
values before `docker compose up`:

```bash
bash scripts/check-compose-hardening.sh --env-file .env docker-compose.prod.yml
```

It resolves the file with your `.env` and fails on a missing or non-positive memory, process or
tmpfs limit.

The API container has no shell or curl, so its health check is the binary itself:
`docker compose exec app dotnet barakoCMS.dll --health-probe /health/ready` exits 0 on a 2xx and 1
otherwise, and without a path it asks `/health/live`.

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

## The summary a console reads

`GET /api/monitoring/metrics` (capability `view_monitoring`) is a small JSON summary for a console,
not a replacement for `/metrics`. Besides the totals since the process started it carries:

- `startedAt` and `uptimeSeconds`: when this process started and how long it has run.
- `last24Hours`: `requests`, `errors`, `errorRate` and `averageResponseTime` over the trailing day,
  kept per minute, plus `p95ResponseTime` and `cacheHitRate` with its count `cacheHits`.

`p95ResponseTime` is the upper bound of the histogram bucket the 95th percentile falls in (1, 2, 5,
10, 15, 20, 30, 40, 50, 75, 100, 150, 200, 300, 500, 750 ms and up to 10 s), so it can overstate by
one bucket (a response slower than 10 s reads as 10 s). A cache hit is a `304 Not Modified` to a
client holding the current ETag, or a response the output cache served without running the
endpoint, counted against every successful GET and HEAD other than the health probes and
`/metrics`. Each instance counts its own requests, and a restart starts the window again.

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

The ask URL uses the service name, so `app` must be in `AllowedHosts` (the shipped
`docker-compose.prod.yml` default lists it beside `DOMAIN_API`).

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
appears in. Most CDNs strip both tag headers before the browser. By default the tag headers stay
under 1024 bytes each, inside a default nginx `proxy_buffer_size`. If you raise
`Delivery:CacheTags:MaxHeaderLength`, both headers grow to that size, so raise `proxy_buffer_size`
to at least twice that plus about 2k, together with `proxy_buffers` and `proxy_busy_buffers_size` (for
16384 bytes: `proxy_buffer_size 64k; proxy_buffers 8 16k; proxy_busy_buffers_size 64k;`), or nginx
answers 502. nginx refuses a larger `proxy_buffer_size` on its own. See "Tags" in `docs/delivery-api.md`. See "Cache classes, validators and tags" in
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

## Secrets as files

The JWT key, the admin password and the database passwords can live in files instead of `.env`
(Docker secrets, or a Kubernetes secret volume). The API reads a directory of files as configuration:
`BARAKO_SECRETS_DIR`, default `/run/secrets`, blank means the default. A file name is the
configuration key with `__` for the section separator, so `/run/secrets/JWT__Key` is `JWT:Key`. A
missing directory is fine. Both shipped images read the directory. A custom host calls
`builder.Configuration.AddSecretFilesFromEnvironment()` right after `WebApplication.CreateBuilder`,
before anything reads configuration. A file the API cannot read, or a directory it cannot list, stops startup with a message that names
the file or directory and never a content.

Secret files are read after every other source, so a file wins over an environment variable, a
command-line argument and an `appsettings` value for the same key. The startup guards read the same configuration, so an example
key or password in a file is refused outside Development exactly as it is in `.env`. Trailing
carriage returns and line feeds are removed from the value, so `echo key > file` and an editor that
adds a final newline both give the key you typed. A UTF-8 byte order mark is dropped, and an empty file sets an empty value
(for the admin password that means one is generated). Nothing else is trimmed.

To use it with the production stack, add the override with a second `-f` and set
`SECRETS_AS_FILES=true`, in `.env` or the shell. That variable only lets the required `JWT_KEY`,
`ADMIN_PASSWORD` and `DB_APP_PASSWORD` checks in `docker-compose.prod.yml` pass while those are
unset; the override blanks them in the containers. Without the override the variable gives the stack
no secrets, so Postgres will not initialise and the API will refuse to start. Everything else in `.env` still applies.

```bash
mkdir secrets
openssl rand -hex 24 | tr -d '\n' > secrets/jwt_key
openssl rand -hex 24 | tr -d '\n' > secrets/admin_password
openssl rand -hex 24 | tr -d '\n' > secrets/db_password
openssl rand -hex 24 | tr -d '\n' > secrets/db_app_password
printf 'Host=postgres;Database=barako_cms;Username=barako_app;Password=%s' "$(cat secrets/db_app_password)" > secrets/db_connection_string
sudo chown 1654:1654 secrets/*
sudo chmod 0440 secrets/*
SECRETS_AS_FILES=true docker compose -f docker-compose.prod.yml -f docker-compose.secrets.yml up -d
```

The API runs as uid 1654 in a read-only container, so the files are owned by `1654:1654` with mode
`0440`. The owner's password file is mounted into Postgres (as `POSTGRES_PASSWORD_FILE`), the role
job and the backup job, and the app role's password file into the role job, which join group 1654
to read them. `secrets/` is in `.gitignore`.

The API takes its database password inside `ConnectionStrings:DefaultConnection` and has no separate
password key, so the connection string is the secret (`db_connection_string`, mounted as
`ConnectionStrings__DefaultConnection`). It names the app role and that role's password: keep the
password the same as `db_app_password`, which is the one the role job sets, and the user the same as
`DB_APP_USER` if you set that. The database name is the same as `DB_NAME` if you set it. Without the override nothing changes: `docker-compose.prod.yml` declares no
secrets, because compose refuses to start when a declared secret file is missing.

## Database roles

Two roles are in play. The owner (`DB_USER`, default `postgres`, password `DB_PASSWORD`) is a
superuser, because it is the one Postgres is initialised with. The app role (`DB_APP_USER`, default
`barako_app`, password `DB_APP_PASSWORD`) is a login role with no superuser and no `BYPASSRLS`, and
it owns what is in schema `public`. The API connects as the app role, so Postgres binds it to the
row level security policies Marten creates, but only when `Tenancy:DatabaseEnforcement` is on. It is
off by default, and without it the policies do not exist. A superuser is never bound by them. See
[Tenancy at the database](tenancy-at-the-database.md).

Before the API starts, the `postgres-extensions` job runs `scripts/db-app-role.sql` as the owner. It
creates the app role or updates it, sets its password, installs `pg_stat_statements` and gives the
role ownership of the tables. The API waits for the job to finish, and does not start if the job
fails: `docker compose logs postgres-extensions` says why. The quickstart does the same with
`quickstart/scripts/db-app-role.sql`. `docker-compose.yml` and `docker-compose.hub.yml` are for
development and stay on the owner role with no job.

- Set `DB_APP_PASSWORD` in `.env`. The stack refuses to start without it, and the API refuses an
  example value. Keep it different from `DB_PASSWORD`.
- If you set `DATABASE_URL`, it wins over the connection string the compose file builds, so point it
  at the app role too. A `DATABASE_URL` that names the owner keeps the API on a superuser.
- To rotate the app role's password, change `DB_APP_PASSWORD` (and `db_app_password` and
  `db_connection_string` if you use secret files) and run `docker compose up -d`. The job sets the
  new password on every run.
- Changing `DB_APP_USER` creates a new role and leaves the old one with `LOGIN` and ownership of
  nothing. Drop it as the owner once the API is running as the new role:
  `DROP ROLE old_name;`. If Postgres says the role still owns objects or privileges, run
  `REASSIGN OWNED BY old_name TO new_name; DROP OWNED BY old_name; DROP ROLE old_name;` in each
  database that uses it.
- Several databases in one cluster that use the same app role name run their jobs one at a time. The
  script takes a lock per database, and that does not cover a second database.
- Run ad hoc SQL, DML and anything that calls an application function as the app role, never as the
  owner: `docker compose exec postgres psql -U barako_app -d barako_cms` (or the names you set). The
  owner runs the `postgres-extensions` job, the migrations below and nothing else.
- These hand-run migrations run as the owner, because each one checks for a role that can bypass row
  level security and stops otherwise, or says in its header to run as a superuser. Run each with the
  search path pinned to the built in schema, so it uses only built in functions and operators, and
  run them before the `postgres-extensions` job when the volume predates the app role:

  ```bash
  PGOPTIONS='-c search_path=pg_catalog,pg_temp' psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.0.0/role-tenancy.sql
  ```

  The files are `migrations/4.6.0/sensitivity-by-capability.sql`,
  `migrations/4.6.0/rollback-sensitivity-by-capability.sql`, `migrations/4.6.0/tenant-profile-to-site.sql`,
  `migrations/4.6.0/rollback-tenant-profile-to-site.sql`, `migrations/4.7.0/tenant-policy-restore.sql`,
  `migrations/5.0.0/role-tenancy.sql`, `migrations/5.0.0/rollback-role-tenancy.sql`,
  `migrations/5.0.0/site-field-schemas.sql`, `migrations/5.0.0/rollback-site-field-schemas.sql`,
  `migrations/5.6.1/site-fonts-schema.sql` and `migrations/5.6.1/rollback-site-fonts-schema.sql`.
  Each header gives the exact `psql` flags. Every name in these files resolves in `pg_catalog` or is
  written as `public.<name>`, so they run the same with the pin on. `scripts/upgrade-check.sh` runs
  them that way in CI. The two rollback files also set the pin themselves.
- From 6.0 no new migration needs a superuser. See [Migrations](migrations.md).

Backups are taken with `pg_dump --no-owner --no-privileges`, so whoever restores owns what the
restore creates. Restores run as the app role: the `postgres-extensions` job creates the role and
the extensions in the empty database first, the restore stream drops the `CREATE EXTENSION` and
`COMMENT ON EXTENSION` lines, and the dump is loaded with `PGOPTIONS='-c role=barako_app'`. Then
`docker compose up -d` starts the API. `scripts/restore-db.sh` does this, and
[Backup and restore](backup-and-restore.md) has the commands.

## Upgrading

Change `BARAKO_TAG` first, then pull and stop the API. From 6.0 the API connects as the app role,
which the `postgres-extensions` job creates and hands the tables to, so the job runs before any
`db-migrate` or `db-assert`. On a 5.x volume the role does not exist yet and those commands cannot
sign in. Where the role exists but the job has not run, a migration's DDL fails as a non-owner.

```bash
docker compose -f docker-compose.prod.yml pull
docker compose -f docker-compose.prod.yml stop app
docker compose -f docker-compose.prod.yml run --rm postgres-extensions
echo "job exit code: $?"
```

Carry on only when the exit code is 0. Then check what the new build would do:

```bash
docker compose -f docker-compose.prod.yml run --rm --no-deps app db-migrate --status
```

`--status` only reads. It exits 0 when this database has had every SQL file the new build ships, and
then the upgrade is:

```bash
docker compose -f docker-compose.prod.yml run --rm --no-deps app db-assert
docker compose -f docker-compose.prod.yml up -d
```

When `--status` lists a migration as `pending` or `not-needed` and exits 1, migrate first:

```bash
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

