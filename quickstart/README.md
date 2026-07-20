# barakoCMS quickstart

Run a complete barakoCMS — the **full suite** (engine + every module), the **admin UI**, and
**Postgres** — from prebuilt images. No build step, no .NET or Node toolchain. You edit one `.env`
and start it.

## What you get

- **CMS API** (`ghcr.io/baryodev/barako-cms`) — core plus every module: Accounting, Analytics
  (Umami), Email (Resend), Feature flags, Diagnostics, Import, Files, Device trust, External auth,
  Portability.
- **Admin UI** (`ghcr.io/baryodev/barako-admin`) — content modeling, entries, users/roles, and a
  section for each installed module (they appear when their keys are set).
- **Postgres 16** with a persistent volume.

Every module is already in the image. Each one stays **off or on a safe mock** until you provide its
keys, so an empty-but-valid `.env` boots a working CMS you can grow into.

## Run it

```bash
# from this quickstart/ folder
cp .env.example .env
#   edit .env — at minimum: DB_PASSWORD, JWT_KEY, ADMIN_PASSWORD
docker compose up -d
```

Then open:

| | URL |
| --- | --- |
| Admin UI | http://localhost:3000 |
| CMS API | http://localhost:5005 |
| API health | http://localhost:5005/health |

Sign in to the admin with `ADMIN_USERNAME` / `ADMIN_PASSWORD` from your `.env`.

## Configuration

Full reference is in [`.env.example`](.env.example). The essentials:

### Required
| Variable | Notes |
| --- | --- |
| `DB_PASSWORD` | Postgres password. |
| `JWT_KEY` | Token signing key — **must be 32+ characters** or the API won't start. |
| `ADMIN_USERNAME` / `ADMIN_PASSWORD` | The first admin account, seeded on first boot. |

### Turning modules on
Each module block in `.env` is optional. Fill it in and `docker compose up -d` again to apply.

- **Email (Resend)** — set `RESEND_API_KEY` + `RESEND_FROM` to actually send mail (otherwise emails
  are logged by a mock). `RESEND_WEBHOOK_SECRET` enables bounce/complaint tracking.
- **Analytics (Umami)** — point `UMAMI_BASEURL` at your Umami instance, set `UMAMI_ENABLED=true` and
  a read account (`UMAMI_USERNAME` / `UMAMI_PASSWORD`). The admin's Analytics section then shows
  visitors, top pages, referrers, devices and more, and can register new sites.
- **Social sign-in (ExternalAuth)** — set `EXTERNALAUTH_ENABLED=true` and the client id/secret for
  each provider you want (Google, GitHub, Facebook, LinkedIn).
- **Device trust** — `DEVICETRUST_ENFORCE=true` requires new devices to be approved via an emailed
  code.

## Behind a domain (production)

The compose exposes the API and admin on localhost. In front of them put a reverse proxy (nginx,
Caddy, Traefik) terminating TLS, then set:

```env
ASPNETCORE_ENVIRONMENT=Production
PUBLIC_API_URL=https://cms.example.com      # what the browser calls
ALLOWED_ORIGINS=https://admin.example.com   # where the admin is served
BARAKO_TAG=3.1.0                            # pin a release rather than :latest
```

If you serve the admin under a sub-path (e.g. `example.com/cms`), that needs a base-path build of the
admin image — see the main repo's deploy notes; the default image serves at the root.

## Upgrading

```bash
docker compose pull && docker compose up -d
```

Schema migrations run automatically on start. Pin `BARAKO_TAG` to a specific version for
reproducible, deliberate upgrades.

## Data & backup

Postgres data lives in the `pgdata` volume. Back it up with `pg_dump`, e.g.:

```bash
docker compose exec postgres pg_dump -U postgres barakocms > backup.sql
```

## Lean core instead of the full suite

Want only some modules? Swap the API image for the core-only build
(`ghcr.io/baryodev/barako-cms-decaf`) and register just the modules you need in your own host. The
suite here is the batteries-included default.
