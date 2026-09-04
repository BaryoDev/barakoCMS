# Handoff: barakoBrew admin redesign and barakoCMS site redesign

> Kept as design history. The `admin/` and `site/` paths named below moved out of this repository:
> the console is [BaryoDev/barakoBrew](https://github.com/BaryoDev/barakoBrew) and the site has its own.

## Overview

A 2026 redesign of the **barakoCMS** marketing site and its Next.js admin, which is renamed
**barakoBrew** in the admin chrome. The work covers nine built screens plus four proposal screens
for gaps the repo's own `ROADMAP.md` names but has not built yet.

Source of truth for the current state: `BaryoDev/barakoCMS` @ `master`.

The goal the user set: look credible next to Supabase and similar, convert .NET developers faster,
show the product instead of describing it, and fix an admin they described as "dull and boring and
not intuitive".

## About the design files

The files in this bundle are **design references created in HTML**. They are prototypes showing
intended look and behavior, not production code to copy.

The task is to **recreate these designs inside the existing admin app** at `admin/` — Next.js 15 App
Router, React, Tailwind v4, shadcn/ui, TanStack Query — using its established patterns. Do not port
the HTML. Every screen below maps to a real route that already exists (or a new one that follows the
same conventions), and every component below has a shadcn equivalent already vendored in
`admin/src/components/ui/`.

The design files are **Design Components** (`.dc.html`). They open directly in a browser. They need
`support.js` and `icon-sprite.js` sitting next to them, both included in this bundle. Open them from
a local static server rather than `file://` so the sprite script loads.

## Fidelity

**High fidelity.** Final colors, typography, spacing, radii, shadows, and copy. Recreate pixel
perfectly, substituting the codebase's own primitives where they already match (Button, Card, Table,
Badge, Select, Switch, Tabs, Sidebar are all present).

Two exceptions:
- **Media thumbnails** are deliberately striped SVG placeholders with monospace filename labels. They
  mark where real imagery goes; do not ship the stripes.
- The **Proposed** file is a design proposal for unbuilt features. Treat it as a spec to discuss,
  not a ticket to implement.

---

## What changes, at a glance

The current admin is Bootswatch Yeti: `--radius: 0rem`, primary `#008cba` (darkened to `#007da6`
for AA), Open Sans with 300-weight headings, `#dee2e6` borders. It is defined in
`admin/src/app/globals.css`.

The redesign replaces that theme wholesale:

| | Current | New |
| --- | --- | --- |
| Radius | `0rem` | 14px cards, 10-11px controls, 999px pills |
| Primary | `#007da6` teal-blue | `#5A46D6` indigo |
| Heading font | Open Sans 300 | Sora 600 |
| Body font | Open Sans | Manrope |
| Mono | Geist Mono | JetBrains Mono |
| Surface | `#ffffff` on `#f8f9fa` sidebar | `#ffffff` panel on `#FAFAFC` page |
| Border | `#dee2e6` | `#E7E8F1` |
| Elevation | `shadow-sm` only | layered soft shadows |
| Sidebar | 19 flat items, 256px | grouped with counts and badges, 248px, padded rail |

Light theme only for now. Dark mode was explicitly deferred by the user.

---

## Design tokens

Author these as CSS variables in `admin/src/app/globals.css`, replacing the Yeti block. Names below
map onto the shadcn token names the codebase already uses.

### Color

| Token | Hex | shadcn mapping | Use |
| --- | --- | --- | --- |
| page | `#FAFAFC` | `--background` | app background behind panels |
| panel | `#FFFFFF` | `--card`, `--popover` | cards, panels, popovers |
| sunken | `#F2F3F9` | `--muted`, `--secondary` | segmented-control track, chips, table head |
| line | `#E7E8F1` | `--border`, `--input` | every 1px border |
| ink | `#101223` | `--foreground` | primary text |
| ink-2 | `#4A4E66` | `--secondary-foreground` | body copy, secondary text |
| muted | `#63687D` | `--muted-foreground` | labels, metadata, captions |
| faint | `#6E7387` | — | timestamps, table column heads |
| disabled | `#C3C6D6` | — | decorative icons, disabled controls |
| accent | `#5A46D6` | `--primary`, `--ring` | primary buttons, active nav, links |
| accent-hover | `#4A38C0` | — | primary button hover |
| accent-ink | `#4034A8` | — | accent text on tinted background |
| accent-deep | `#241C6B` | — | accent text on `accent-soft` panels |
| accent-soft | `#EEEBFD` | `--accent` | tinted panels, active nav background |
| accent-border | `#DED8FB` | — | border on tinted panels |
| success | `#0B7A6B` on `#E6F7F4` | `--success` | Published, Healthy |
| warning | `#8A5A10` on `#FDF2E3` | `--warning` | Scheduled, bounces, In review |
| danger | `#A22C2C` on `#FDECEC` | `--destructive` | Unbalanced, errors, 403 |

**Accessibility, non-negotiable.** `globals.css` documents two prior WCAG remediations and states
the project gates on an axe pass. `muted` at `#63687D` is 5.44:1 on `#FFFFFF` and 4.91:1 on the
`#F2F3F9` / `#FAFAFC` tints. Do not lighten it. The earlier draft used `#8B8FA6` (3.19:1) and failed —
that is lighter than the `#888` your own CSS comments already reject.

### Typography

```
--font-display: 'Sora',      ui-sans-serif, system-ui, sans-serif;  /* 600 */
--font-sans:    'Manrope',   ui-sans-serif, system-ui, sans-serif;  /* 400 500 600 700 800 */
--font-mono:    'JetBrains Mono', ui-monospace, monospace;          /* 400 500 700 */
```

Self-host with `next/font/google` exactly as the site already does, so no request leaves the box at
runtime.

| Role | Family | Size | Weight | Tracking |
| --- | --- | --- | --- | --- |
| Hero h1 | Sora | 76px / 1.02 | 600 | -0.04em |
| Section h2 | Sora | 42px / 1.08 | 600 | -0.03em |
| Page title | Sora | 18px | 600 | -0.025em |
| Screen h2 | Sora | 21-26px | 600 | -0.03em |
| Card title | Sora | 14.5-15px | 600 | normal |
| Body | Manrope | 13.5-15.5px / 1.6-1.7 | 400-500 | normal |
| Label | Manrope | 12-12.5px | 700 | normal |
| Eyebrow | JetBrains Mono | 10-11px uppercase | 400-700 | 0.14-0.16em |
| Metric | JetBrains Mono | 28-30px / 1 | 700 | -0.035em, `tabular-nums` |
| Meta / code | JetBrains Mono | 11-12.5px | 400-700 | normal |

**Rule: every machine-produced value is monospace with `tabular-nums`.** Counts, versions, slugs,
timestamps, durations, IDs, latencies, event names, API paths. Human prose is Manrope. This single
rule does most of the work of making the admin feel like a developer tool.

### Spacing, radius, elevation

```
radius:  card 14px · control 10-11px · chip 8-9px · inner 10px · pill 999px
padding: card 16-18px · panel header 15px 18px · main 22-24px · table cell 13px 24px
gap:     grid 12px · stack 14-16px · inline 8-12px
shadow:  card       0 1px 2px  rgba(16,18,35,.04)
         raised     0 1px 3px  rgba(16,18,35,.06)
         popover    0 24px 56px -20px rgba(16,18,35,.32)
         accent btn 0 5px 14px -7px rgba(90,70,214,.8)
         hero shot  0 30px 70px -34px rgba(16,18,35,.38)
control heights: 50px hero CTA · 44px form · 38-40px default · 36px compact · 34px small
```

---

## Screens

### File: `barakoCMS 2026 - Signal.dc.html` — nine screens

#### 1. Site landing (`site/app/page.tsx`)

Full-width, 1440px design width, sections stacked.

- **Nav, 72px.** Bean mark 30px + "BarakoCMS" Sora 18/600. Pill group on `#F2F3F9` with 4px padding;
  active pill is white with `0 1px 2px rgba(16,18,35,.08)`. Right side: GitHub with star count in
  mono, "Sign in" ghost, "Get started" solid `#101223`.
- **Hero, centered, 56px top padding.** Announcement pill (white, 1px `#E7E8F1`, 999px) with a `NEW`
  badge in `#EEEBFD`/`#4034A8`. h1 **"Ship the boring 80% on day one"**, Sora 76/1.02/600/-0.04em,
  `max-width: 19ch`, `text-wrap: balance`. Sub-paragraph 18px/1.6 `#4A4E66`, `max-width: 66ch`:
  *"Content modelling, users, roles, per-field permissions, workflow, audit history and
  multi-tenancy, event-sourced on PostgreSQL, exposed as a REST delivery API. Bring your own
  frontend."* Then a 50px accent CTA "Deploy in 3 minutes" beside a white pill holding
  `$ docker compose up -d` in mono with a copy button. Footnote: *"MPL-2.0, self-hosted, no seat cap,
  no revenue cap, no metered AI."*
- **Product shot.** A browser frame on a `#F2F3F9` plinth with `22px 22px 0 0` radius, the frame
  itself `16px 16px 0 0`, bleeding off the bottom of the section. Inside is the real entry editor:
  icon rail, form, and an event-stream sidebar. **This is the single most important element on the
  page** — the user asked specifically to show the product.
- **What you inherit.** Six cards, 3-up, 16px radius, each with a 38px `#EEEBFD` icon tile. Content
  lifted from the current site's roast-block list.
- **Three lines to start.** Two columns: copy left, dark `#101223` code panel right showing
  `AddBarakoCMS` with three modules registered. Syntax tints: keyword `#8B8FA6`→ use `#63687D`,
  method `#A99BF7`, type `#7FD1C1`, plain `#E6E7F3`.
- **Comparison table.** The existing Umbraco table, restyled: header row on `#FAFAFC`, check icon
  beside each included capability, honest closing paragraph retained verbatim.
- **Agency CTA.** Dark `#101223` block, 20px radius, with the bean at 340px and 16% opacity bleeding
  off the top-right corner. Four two-column points over hairline `#2C2F52` rules.
- **Footer.** Bean 20px + wordmark, license, links.

#### 2. Admin sign in (`admin/src/app/login/page.tsx`)

Centered 340px column on `#FAFAFC`, with the bean at 300px / 7% opacity bleeding off the bottom-left.
Bean 44px above a Sora 24/600 heading **"Sign in to barakoBrew"** ("Brew" in `#5A46D6`) and the
workspace name beneath. Form is a white 16px-radius card with `0 4px 16px -8px rgba(16,18,35,.12)`:
username, password with a reveal toggle and a "Forgot?" link, 44px accent submit. Below an `OR`
divider, two outline buttons — "Email me a sign-in code" and "Continue with GitHub" — which the
`DeviceTrust` and `ExternalAuth` modules already support. Footnote states the real lockout policy:
five failed attempts locks for 15 minutes, a new device asks for an emailed code.

#### 3. Admin Overview (`admin/src/app/(admin)/page.tsx`)

The shell all admin screens share.

- **Sidebar, 248px, on the page background, 16px padding.** Bean 30px + "barakoBrew" + version in
  mono. Then a 40px search button with a `⌘K` kbd. Then a tenant switcher button showing the tenant
  initial in an accent tile, its name, and "3 tenants available".
  Nav is **grouped, not flat** — this is the main fix to "not intuitive". Primary group (Overview,
  Entries, Content types, Workflows) at 38px with mono counts right-aligned; then `Access`,
  `Modules, 5 installed` (with an "Add a module" accent row), and `System` at 34px under 10.5px/800
  uppercase group labels in `#6E7387`. Unread counts are pills: warning tint for email events,
  danger tint for errors. Active item is a white card with `0 1px 2px rgba(16,18,35,.06)` and an
  accent icon. Footer is the account card.
- **Content panel.** White, 14px radius, `margin: 16px 16px 16px 0`, so the sidebar reads as a rail
  rather than a column. Header 60px: page title, health pill, a bolt button with an unread dot,
  primary "New entry".
- **Body.** A greeting line that states the day's actual situation. A tinted `#EEEBFD` banner for
  scheduled publishing. Four stat cards with mono metrics — Entries, Published (with a three-segment
  proportion bar), Delivery API 24h, Visitors 7d with a sparkline. Then a 1.55/1 split: **"Needs
  you"** (a list of things requiring action, replacing the old "Latest entries" which required no
  decision) and, in the rail, **Event stream** and **System**.

The old Overview had three empty stat cards and a list of recent entries. The new one leads with what
requires a decision, and surfaces the event stream, which is the product's actual differentiator.

#### 4. Entries (`admin/src/app/(admin)/content/page.tsx`)

Header with a live count pill. Filter bar: 280px search, a status segmented control
(All / Published / Draft / Scheduled / Archived), and a type dropdown. Table: `#FAFAFC` head with
10.5px/800 uppercase labels, 13px rows, entry title 700, type in mono, status pill, version in mono
right-aligned, relative time right-aligned. Row hover `#FAFAFC`. Footer: "1 to 20 of 148" with prev
and next. Status pills use the tint pairs from the token table; add a lock icon and `Private` for
types not publicly deliverable.

#### 5. Entry editor (`admin/src/app/(admin)/content/[id]/page.tsx`)

Header: back button, title, status pill, a `Saved` confirmation pill, and a mono meta line reading
`article · v7 · publishes today 09:00 · /api/public/article/spring-roast`. Actions: Preview, Archive,
Publish now.

Body is a two-pane split. Left is the form, with a pill tab group — **Content / Scheduling /
Permissions / History**. Promoting Scheduling and Permissions out of the field list is a deliberate
change: they were buried. Each field has a label plus a mono type hint (`string · required`,
`slug · required`, `markdown`). The body field is a bordered composer with a toolbar and a live word
count. `Publish at` renders as an accent-tinted control when a schedule is armed.

Right pane, 290px on `#FAFAFC`: the **event stream** as version cards, current version accent-bordered,
older ones stepped down in opacity, each with a "Restore this version" button; then **Workflows
watching**, listing the automations that will fire on publish.

#### 6. Content type builder (`admin/src/app/(admin)/schemas/new/page.tsx`)

Display name and API name side by side, the slug auto-derived and marked `auto`. A public-delivery
toggle whose description shows the live route it would expose. Then the field list: draggable rows,
each with a type icon in an accent tile, display name, `apiName · type` in mono, and a `Required`
pill. **One field is shown expanded mid-edit** — type, sensitivity, required — because per-field
sensitivity is the feature most worth making visible and it was previously buried. Below, a dashed
panel of type chips to add a field.

#### 7. Workflows (`admin/src/app/(admin)/workflows/page.tsx`, `workflows/new/page.tsx`)

Two panes. Left, 336px: a filterable list where each workflow card shows its name, an enable toggle,
`triggerContentType · triggerEvent` in mono, action-type chips, and a run-health line (green for
clean, danger when a run failed). Paused workflows drop to 0.72 opacity.

Right: a builder on a vertical timeline with three nodes — **When** (accent node), **Only when**, and
**Then do**. When is a sentence: "an entry of type [Article] is [Created | Updated]". Only when is a
list of `field equals value` rows plus a dashed "Add condition". Then do is an ordered action list;
the first is expanded showing its parameters. Below, the template-variable palette scoped to the
trigger type, and a "Last runs" log with dry runs marked.

Everything here follows `types/workflow.ts`: trigger event is only `Created` or `Updated`, the six
action types are Email, SMS, Webhook, CreateTask, UpdateField and Conditional, and parameters accept
`{{variable}}` templates.

> **Implementation note.** Rendering literal `{{variable}}` inside a Design Component required
> escaping, because `{{ }}` is the template hole syntax. That constraint does not exist in React —
> just render the strings.

#### 8. Analytics (`admin/src/app/(admin)/analytics/page.tsx`)

Header: website picker with a status dot, an "active now" pill, and a 24h/7d/30d/90d segmented
control. Four summary cards with mono metrics and a change line against the previous period, colored
by direction. A "Pageviews over time" chart with the current period as a filled accent line and the
previous period as a dashed `#C9C1F5` line behind it. Then six breakdown cards, 3-up twice: Top pages
(mono), Referrers, Countries (flag + name), Devices, Operating systems, Browsers. Each row is a
proportional 10%-tint bar behind the label with a right-aligned mono count — the Umami-style
breakdown the current page already implements.

#### 9. Tenant switcher, open

A 330px popover anchored 6px below the trigger, `0 24px 56px -20px rgba(16,18,35,.32)`. Contains a
find field, the user's tenants each with `slug · role` in mono and a check on the current one, a
divider, then **Home** for the default platform partition. Footer: "Create a tenant" and the line
*"Switching re-issues your token for that tenant. Everything on screen reloads under it, and your
role can differ per tenant."* That sentence is the behavior documented in
`hooks/use-tenants.ts` — the switch calls `/api/me/switch` and invalidates every query.

### File: `Proposed - barakoBrew gaps and modules.dc.html` — four proposals

Not built in the repo. Drawn from `ROADMAP.md` and the README's "what it does not do yet" table.

- **Modules** — answers issue #185. The instance reports what it actually loaded from
  `/api/meta/modules`, with per-module document, route and seeder counts, plus the modules that ship
  in the image but stay off until keyed. A NuGet strip below reuses the `barakocms-module` tag
  discovery the marketplace site already does.
- **Agents (MCP)** — roadmap 3.26.0. Tools generated from the content schema, routed through existing
  RBAC rather than a separate agent permission model. Guardrails match the MCP spec's MUSTs: rate
  limiting, input validation, audit logging.
- **Media** — a named gap. Grid with focal-point picker, named variants, and reference counting that
  blocks deletion while an entry uses the file.
- **Review** — a named gap. Draft, review, changes requested, approved. The screen explicitly states
  that "workflow" in barakoBrew means automation rules and this is something else, because
  `ROADMAP.md` warns about exactly that collision.

Plus a table of **nine candidate primitives** run through the README's own module-or-core tests.
Seven are modules: Media, Forms, Redirects, Seo, Mcp, Search.Postgres, Notifications. Two fail and
are marked core: Review and Localization.

**The structural finding worth reading:** three of the nine want a Marten projection, and
`IModuleSchema` exposes only `For<T>()`. `DECISIONS.md` already flags that as the thing to change
first if a module ever needs one.

### Reference files

- `Current State - barakoCMS.dc.html` — the existing admin and site rebuilt from source at exact
  Yeti values. Use for before-and-after comparison.
- `Redesign Directions - barakoCMS.dc.html` — the three explored directions. **1c "Signal" was
  chosen**; 1a "Kiln" and 1b "Ledger" are archived context.

---

## Interactions and behavior

- **Hover.** Nav items to `#F2F3F9`. Cards to `border-color: #DED8FB` with
  `0 8px 24px -12px rgba(90,70,214,.22)`. Table rows to `#FAFAFC`. Accent buttons to `#4A38C0`.
  Outline buttons to `border-color: #5A46D6; color: #4034A8`. All at ~150ms.
- **Live indicators.** Health dots and event-stream dots pulse `opacity: 1 → .3` over 1.8-2.4s.
  Must be disabled under `prefers-reduced-motion`, which `globals.css` already handles — but note its
  existing comment: Radix needs near-zero durations rather than `none`, or dialogs never unmount.
- **Segmented controls.** Track `#F2F3F9`, 3px padding, active thumb white with
  `0 1px 2px rgba(16,18,35,.08)`.
- **Toggles.** 34-38px wide, accent when on, `#E7E8F1` with a shadowed white thumb when off.
- **Command menu.** `⌘K`, already implemented in `components/command-menu.tsx`.
- **Focus.** Keep the shadcn 3px `ring-ring/50` treatment with `--ring: #5A46D6`.

## State management

No new patterns. Everything maps to existing TanStack Query hooks in `admin/src/hooks/`:
`use-schemas`, `use-contents`, `use-workflows`, `use-analytics`, `use-monitoring`, `use-tenants`,
`use-meta`. New screens would need `use-modules` (#185), `use-media`, and `use-review`.

The Overview's "Needs you" list is a derived view — scheduled entries, drafts past an age, plus
module-reported problems — not a new endpoint.

## Assets

- **Bean logo.** Exact geometry from `site/app/bean.tsx`, unchanged: `viewBox="0 0 128 128"`,
  `rotate(-32 64 64)`, `ellipse rx=33 ry=50`, highlight `ellipse cx=52 cy=44 rx=9 ry=17` at 0.35
  opacity, crease `path d="M64 17 C 51 41, 77 55, 64 64 C 51 73, 77 87, 64 111"` stroked 6.5 round.
  **Only the fill changed**: the coffee gradient becomes
  `#9C8DF5 → #5A46D6 (0.55) → #33257F`, crease `#241C6B`. Used at 20, 24, 30 and 44px, and oversized
  at 7% and 16% opacity as a watermark.
- **Icons.** The repo's own vendored Line Awesome set from `admin/src/components/icons/index.tsx`,
  extracted into `icon-sprite.js` (72 symbols, `viewBox="0 0 32 32"`, `fill: currentColor`, ids like
  `#ic-content`). **In the real app, keep importing the existing React icon components** — the sprite
  exists only so the HTML prototypes could use the real glyphs. Nothing was hand-drawn.
- **Fonts.** Sora, Manrope, JetBrains Mono. All on Google Fonts.
- **Imagery.** None. Media thumbnails are striped placeholders; supply real assets.

## Files in this bundle

```
barakoCMS 2026 - Signal.dc.html              nine built screens — the deliverable
Proposed - barakoBrew gaps and modules.dc.html   four proposal screens + module table
Current State - barakoCMS.dc.html            the existing UI, rebuilt from source
Redesign Directions - barakoCMS.dc.html      the three explored directions
icon-sprite.js                               72 Line Awesome symbols from the repo
support.js                                   runtime the .dc.html files need
github.md                                    repo association and screen-to-source map
screenshots/                                 1x PNG of every screen, named per section below
```

### Screenshot index

| File | Screen | Section above |
| --- | --- | --- |
| `01-site-landing.png` | Site landing | Screens 1 |
| `02-admin-overview.png` | Admin Overview | Screens 3 |
| `03-admin-entries.png` | Entries | Screens 4 |
| `04-admin-sign-in.png` | Admin sign in | Screens 2 |
| `05-admin-entry-editor.png` | Entry editor | Screens 5 |
| `06-admin-content-type-builder.png` | Content type builder | Screens 6 |
| `07-admin-workflows.png` | Workflows | Screens 7 |
| `08-admin-analytics.png` | Analytics | Screens 8 |
| `09-admin-tenant-switcher.png` | Tenant switcher, open | Screens 9 |
| `10-proposed-modules.png` | Modules | Proposals |
| `11-proposed-agents-mcp.png` | Agents (MCP) | Proposals |
| `12-proposed-media.png` | Media | Proposals |
| `13-proposed-review.png` | Review | Proposals |
| `14-proposed-candidate-primitives.png` | Candidate primitives table | Proposals |

The screenshots are a convenience for reading the README away from a browser. **The `.dc.html` files
are authoritative** — they carry hover states, exact computed values, and text you can select and
copy.

## Suggested order of work

1. Swap the theme in `globals.css` — tokens, radius, fonts. Every existing screen improves for free.
2. Rebuild `app-sidebar.tsx` with grouping, counts and badges.
3. Overview, since it is what everyone sees first.
4. Entries, then the entry editor with its event-stream rail.
5. Content type builder, Workflows, Analytics.
6. Then discuss the proposals before building any of them.

## Open questions for the user

- Dark mode was deferred. The token set is designed to invert cleanly, but it has not been drawn.
- The marketing site keeps the name BarakoCMS while the admin becomes barakoBrew. Confirm that split
  is intended.
- Accounting, Users, Roles, Groups, API keys, Audit, Errors, Health and Settings have not been
  redesigned yet. They inherit the theme, but their layouts are unchanged.
