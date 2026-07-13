# BarakoCMS Admin UI

The admin dashboard for [BarakoCMS](https://github.com/BaryoDev/barakoCMS) — a minimalist, coffee-toned interface covering every feature the headless CMS exposes.

**Live demo: <https://playground.baryo.dev/barakocms>** — sign in as `demo_admin` / `BarakoDemo2026!`

![Dashboard](../assets/admin/dashboard.png)

## Run it from Docker Hub

The published image needs no build step:

```bash
docker run -p 3000:3000 \
  -e NEXT_PUBLIC_API_URL=http://localhost:5005 \
  arnelirobles/barako-admin:latest
```

Or bring up the whole stack (API + PostgreSQL + admin) from the repo root:

```bash
docker compose -f docker-compose.hub.yml up -d
```

Open <http://localhost:3000> and sign in with the initial admin account
(`ADMIN_USER` / `ADMIN_PASSWORD` on the API container).

`NEXT_PUBLIC_API_URL` is injected at **container start** by `entrypoint.sh`, which writes
`public/env-config.js`. Pointing the UI at a different API host never needs a rebuild.

### Serving under a sub-path

To host the admin at something like `example.com/barakocms`, bake the base path in at
build time (Next.js resolves `basePath` during the build):

```bash
docker build --build-arg NEXT_BASE_PATH=/barakocms -t barako-admin:subpath ./admin
```

Then proxy `/barakocms/` to the container. Note that Next.js 308-redirects `/barakocms/`
to `/barakocms`, so an nginx rule redirecting the other way will loop — proxy the bare
path instead of redirecting it.

## What it covers

| Area | Capabilities |
| --- | --- |
| Overview | Live stats, latest entries, health summary, quick actions, ⌘K palette |
| Content types | Browse and define schemas with the API's typed fields |
| Entries | Create, edit, publish, archive, filter by type, paginate, **version history with rollback** |
| Workflows | Trigger builder, conditions, actions (Email, SMS, Webhook, CreateTask, UpdateField, Conditional), template variables, validation, **dry-run**, execution logs |
| Users | Assign and remove roles and groups inline |
| Roles | Full CRUD with a per-content-type Create/Read/Update/Delete permission matrix |
| Groups | Full CRUD plus member management |
| Settings | Runtime toggles grouped by category |
| Health | Live health checks, API metrics, Kubernetes status |

Sessions ride the API's rotating refresh tokens: the 15-minute access token renews
automatically, and a single in-flight refresh is shared across concurrent requests so the
backend's replay detection is never tripped.

## Screenshots

| Entries | Entry editor + version history |
| --- | --- |
| ![Entries](../assets/admin/content.png) | ![Entry](../assets/admin/entry.png) |

| Workflows | Role permissions |
| --- | --- |
| ![Workflows](../assets/admin/workflows.png) | ![Roles](../assets/admin/roles.png) |

| Health | Dark mode |
| --- | --- |
| ![Health](../assets/admin/health.png) | ![Dark](../assets/admin/dark.png) |

## Stack

- **Next.js 16** (App Router, React 19, standalone output)
- **shadcn/ui** on Tailwind CSS v4 — every color flows through theme tokens in
  `src/app/globals.css` (warm paper light theme, roast dark theme)
- **Icons**: [Line Awesome by Icons8](https://icons8.com/line-awesome), vendored as
  inline-SVG React components in `src/components/icons/` (regenerate with
  `node scripts/gen-icons.mjs`)
- **TanStack Query** for data, **axios** with auth + refresh interceptors (`src/lib/api.ts`)
- **sonner** toasts, **next-themes**, and a ⌘K command palette

## Local development

```bash
npm install
npm run dev        # http://localhost:3000, expects the API on http://localhost:5006
```

```bash
npm run lint       # eslint (react-compiler rules on)
npm test           # vitest
npm run test:e2e   # playwright
npm run build      # production build
```

## Layout

```
src/
  app/(admin)/          # authenticated pages inside the sidebar shell
  app/login/            # sign-in
  components/ui/        # shadcn/ui primitives
  components/icons/     # generated Icons8 Line Awesome SVGs
  components/patterns/  # PageHeader, EmptyState, StatusBadge, ConfirmDialog, pagination
  hooks/                # TanStack Query hooks per feature area
  lib/api.ts            # axios client, token store, refresh rotation, pagination types
  types/                # API models mirroring the backend
```
