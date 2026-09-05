# Deploying barakoCMS on a VM

One `docker compose up -d` on a clean machine, using the images the release workflow publishes.
Nothing is compiled on the target host.

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

## Putting a shared cache or CDN in front of it

The delivery API (`docs/delivery-api.md`) marks its responses `Cache-Control: public, max-age=60`,
which is an invitation to put a CDN in front of it. Whether that is safe depends on how tenants are
routed (`docs/multi-tenancy.md`), because the response is cacheable per tenant, not globally.

**Safe with a plain CDN, no extra configuration:**

- a single-tenant deployment, where `X-Tenant` is never sent and every request resolves to the same
  tenant, and
- a multi-tenant deployment with one hostname per tenant (a subdomain or a custom domain). The Host
  is part of the URL, and every general-purpose cache already keys on the full URL, so two tenants
  never share a cache entry.

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

## Upgrading

```bash
docker compose -f docker-compose.prod.yml pull
docker compose -f docker-compose.prod.yml up -d
```

Change `BARAKO_TAG` first. Schema migrations run on start. Read
[upgrading-to-4.0.md](upgrading-to-4.0.md) before moving to 4.0, which does not boot without its
migration.

## Backups

`db-backup` dumps nightly to the `postgres_prod_backups` volume, which is its own and not Postgres's,
so losing the database volume does not lose the backups. The script checks pg_dump's exit code,
proves the archive decompresses, and enforces a minimum size before rotating.

Restoring is [backup-and-restore.md](backup-and-restore.md). A backup nobody has restored is not a
backup, so do it once before you have data worth keeping.

## Known rough edges

- **Pick a tag your machine can run.** The `3.21.0` version tags of both images are
  `linux/amd64` only, and so is `barako-cms-decaf:latest`. `barako-cms:latest` carries both amd64
  and arm64. On an arm64 host (Ampere, Graviton, an Apple laptop) pinning `BARAKO_TAG=3.21.0`
  fails the pull with `no matching manifest for linux/arm64/v8`. Check before you pin:

  ```bash
  bash scripts/check-image-platforms.sh ghcr.io/baryodev/barako-cms:$BARAKO_TAG
  bash scripts/check-image-platforms.sh ghcr.io/baryodev/barako-cms-decaf:$BARAKO_TAG
  ```

  Run the line for the image you actually pull; the two are published separately and one can
  carry both architectures while the other does not. That is the same check the release workflow runs on every tag it publishes, and CI proves it
  fails on `3.21.0`. Tracked as #394.

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
  production one builds nothing, and the same images are exercised by the playground deploy, but the
  certificate issuance path is verified by hand. Tracked as #308.

