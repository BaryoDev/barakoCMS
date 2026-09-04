# barakoCMS quickstart

Run the barakoCMS API, the **full suite** (engine + every module), with **Postgres** from prebuilt
images. No build step, no .NET toolchain. You edit one `.env` and start it.

This is the API on its own, and its surface is Swagger. For the API with the console in front of
it, start from [barakoBrew](https://github.com/BaryoDev/barakoBrew): it runs this same image and
puts `ghcr.io/baryodev/barako-admin` in front of it.

## What you get

- **CMS API** (`ghcr.io/baryodev/barako-cms`), core plus every module: Accounting, Analytics
  (Umami), Email (Resend), Feature flags, Diagnostics, Import, Files, Device trust, External auth,
  Portability.
- **Postgres 16** with a persistent volume.
- A **nightly backup** container running the same script the production stack uses.

Every module is already in the image. Each one stays **off or on a safe mock** until you provide its
keys, so an empty-but-valid `.env` boots a working CMS you can grow into.

## Run it

```bash
# from this quickstart/ folder
cp .env.example .env
#   edit .env, at minimum: DB_PASSWORD, JWT_KEY, ADMIN_PASSWORD
docker compose up -d
```

Then:

| | URL |
| --- | --- |
| CMS API | http://localhost:5005 |
| API health | http://localhost:5005/health |
| Swagger | http://localhost:5005/swagger, once `SWAGGER_ENABLED=true` |

Sign in as `ADMIN_USERNAME` / `ADMIN_PASSWORD` from your `.env`, from Swagger or with curl:

```bash
curl -s -X POST http://localhost:5005/api/auth/login -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"YOUR_ADMIN_PASSWORD"}'
```

## Configuration

Full reference is in [`.env.example`](.env.example). The essentials:

### Required
| Variable | Notes |
| --- | --- |
| `DB_PASSWORD` | Postgres password. |
| `JWT_KEY` | Token signing key. **Must be 32+ characters** or the API won't start. |
| `ADMIN_USERNAME` / `ADMIN_PASSWORD` | The first admin account, seeded on first boot. |

### Swagger

Swagger is on automatically in Development and off in Production, which is what this compose
defaults `ASPNETCORE_ENVIRONMENT` to. The compose passes `SWAGGER_ENABLED` through as
`Swagger__Enabled`, so:

```env
SWAGGER_ENABLED=true
```

turns it on at `/swagger` without changing the environment.

### Turning modules on
Each module block in `.env` is optional. Fill it in and `docker compose up -d` again to apply.

Every module in the image is registered unless `BarakoCMS__Modules__Enabled` says otherwise. Unset,
all of them run and the API logs one warning saying so at boot. Set it to a comma-separated list of
module names (`BarakoCMS__Modules__Enabled=Accounting,Files`) to run only those, or to an empty
string for core only. A name that matches nothing stops the boot and lists the names available.
Turning a module off leaves its data in the database; `GET /api/modules` shows each module with
`enabled`. See `MODULES.md` in the repository. `docker-compose.yml` passes a fixed set of variables
to the API and this is not one of them, so it goes under the `api` service's `environment:` block
(there is a commented line ready), not in `.env`.

- **Email (Resend)**: set `RESEND_API_KEY` + `RESEND_FROM` to actually send mail (otherwise emails
  are logged by a mock). `RESEND_WEBHOOK_SECRET` enables bounce/complaint tracking.
- **Analytics (Umami)**: point `UMAMI_BASEURL` at your Umami instance, set `UMAMI_ENABLED=true` and
  a read account (`UMAMI_USERNAME` / `UMAMI_PASSWORD`). The analytics endpoints then answer with
  visitors, top pages, referrers, devices and more, and can register new sites.
- **Social sign-in (ExternalAuth)**: set `EXTERNALAUTH_ENABLED=true` and the client id/secret for
  each provider you want (Google, GitHub, Facebook, LinkedIn).
- **Device trust**: `DEVICETRUST_ENFORCE=true` requires new devices to be approved via an emailed
  code.

## Behind a domain (production)

If you have no proxy already and just want a domain with TLS, use the repository's
`docker-compose.prod.yml` instead of this one. It runs the same published image with Caddy in
front, refuses to start without real secrets, and is the file the project supports for production.
See [docs/deploy-in-production.md](../docs/deploy-in-production.md).

To keep this compose and put your own proxy in front, the compose exposes the API on localhost. In
front of it put a reverse proxy (nginx, Caddy, Traefik) terminating TLS, then set:

```env
ASPNETCORE_ENVIRONMENT=Production
APP_BASE_URL=https://cms.example.com        # what the API puts in links it hands out
ALLOWED_HOSTS=cms.example.com               # the Host headers the API answers to
ALLOWED_ORIGINS=https://admin.example.com   # where the console and your site are served
BARAKO_TAG=3.21.0                           # pin a release rather than :latest
```

`APP_BASE_URL` and `ALLOWED_HOSTS` are the pair that keeps a caller from choosing the origin of the
links this API produces. The `Host` header is written by whoever sent the request, so an RSS feed or
an OAuth `redirect_uri` built from it points wherever the caller likes. Set `APP_BASE_URL` and the
links come from configuration instead. Set `ALLOWED_HOSTS` and anything with another `Host` gets a
400 before it reaches any code, which makes the header trustworthy again and is the better fix if
you can enumerate your hostnames.

With neither set, the RSS feed answers 503 and the OAuth start endpoints fail, naming the setting.
That is deliberate: guessing produces a working-looking link to somebody else's domain.

One trap with `ALLOWED_HOSTS`: a Kubernetes `httpGet` probe sends the pod IP as the `Host` header
unless you add one, so a list of real hostnames makes the probes 400 and the pod never goes ready.
Either add a `Host` header to the probe or leave `ALLOWED_HOSTS` at `*` and rely on `APP_BASE_URL`.

TLS is terminated by the proxy, so add HSTS there:

```nginx
add_header Strict-Transport-Security "max-age=7776000" always;
```

The API sends the same header itself outside Development, but only on requests it can see are
HTTPS, which behind a proxy means only once `FORWARDED_HEADERS_ENABLED` is on. 90 days and no
`includeSubDomains` are the defaults, tunable with `Hsts__MaxAgeDays` and `Hsts__IncludeSubDomains`.
Turn `includeSubDomains` on only once every subdomain is on HTTPS: a browser that has seen it keeps
it for the whole max-age whatever you deploy afterwards.

The app ignores `X-Forwarded-For` until you say which hop to believe, because the header is written
by the caller and honouring it from anywhere lets anyone choose the IP that rate limiting and the
audit log see. Name the proxy and both start working per client:

```env
FORWARDED_HEADERS_ENABLED=true
TRUSTED_PROXY_NETWORK=172.16.0.0/12   # the compose bridge the proxy container sits on
```

Turning it on without a network is a startup failure rather than a silent "trust everyone". If the
proxy runs outside compose, use its address instead via `ForwardedHeaders__KnownProxies__0`.

## Upgrading

```bash
docker compose pull && docker compose up -d
```

Schema migrations run automatically on start. Pin `BARAKO_TAG` to a specific version for
reproducible, deliberate upgrades.

## Data & backup

Postgres data lives in the `pgdata` volume, and `db-backup` dumps it nightly into the `backups`
volume with the same script production runs. For a one-off dump:

```bash
docker compose exec postgres pg_dump -U postgres barakocms > backup.sql
```

## Lean core instead of the full suite

Want only some modules? Swap the API image for the core-only build
(`ghcr.io/baryodev/barako-cms-decaf`) and register just the modules you need in your own host. The
suite here is the batteries-included default.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
