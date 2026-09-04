# Audit: the barakoBrew 2026 redesign against the admin as it stands

> Kept as design history. `admin/src/` moved to [BaryoDev/barakoBrew](https://github.com/BaryoDev/barakoBrew).

Companion to `README.md` in this directory, which is the design contract. This file answers one
question per screen: is the change free with the token swap, is it a component rewrite, or does it
need a route or a server change.

Read against `admin/src/` at the commit this document lands on. Every file named here was opened.

## What is in this directory

- `README.md` and `github.md`, the handoff as delivered, unedited.
- `screenshots/`, all 14 PNGs.
- `prototypes/`, two of the four `.dc.html` files plus the `support.js` and `icon-sprite.js` they need to open. Included because the handoff says the prototypes are authoritative over the screenshots for hover states and exact computed values, and a spec that points at files nobody has is not a spec. Open them from a local static server, not `file://`, or the sprite will not load.
- Not included: `Current State - barakoCMS.dc.html`, because the repo is the current state and it goes stale the moment anything ships, and `Redesign Directions - barakoCMS.dc.html`, because it holds two directions that were rejected and one (Signal) that is now the contract in `README.md`. Both are still in the original download if anyone wants them.

Three categories:

- **Theme.** Comes free once `admin/src/app/globals.css` is swapped. No component is touched.
- **Component.** Existing route, existing data, new markup. Cost is front-end time only.
- **Route or data.** Something the API does not return, or a route that does not exist. Cost
  includes backend work, a test, and usually a version of the API contract.

The distinction is the point of the audit. Category one is nearly free and improves every screen at
once. Category three does not fit in a front-end sprint.

---

## Summary

| # | Screen | Verdict | The expensive part |
| --- | --- | --- | --- |
| 1 | Site landing | Component | The product shot asset does not exist |
| 2 | Admin sign in | Component + data | No password reset exists anywhere in the repo |
| 3 | Admin Overview | **Route or data** | Event stream collides with #229; three of four stat cards have no data source |
| 4 | Entries | **Route or data** | No search, no status filter, no version on the list item |
| 5 | Entry editor | Component + data | Permissions tab has nothing behind it |
| 6 | Content type builder | Component | Drag and drop is a new dependency |
| 7 | Workflows | **Route or data** | A workflow has no enabled flag and no update endpoint |
| 8 | Analytics | Component | Previous-period series is not fetchable |
| 9 | Tenant switcher | Component + data | `/api/me/tenants` does not return a role |
| n/a | Sidebar | Component + data | Counts, the modules count (#185), and "unread" for email events |

Cheapest to most expensive: Analytics, content type builder, tenant switcher, entry editor, sign in,
site landing, Entries, sidebar, Workflows, Overview.

---

## Cross-cutting, before any screen

### Dark mode is not deferred, it ships today and it is the default

The README says "Light theme only for now. Dark mode was explicitly deferred", and describes the
work as "replacing the Yeti block" in `globals.css`. That reads as if dark mode does not exist. It
does:

- `admin/src/app/globals.css` carries a full `.dark` block, 30 tokens, and an
  `@custom-variant dark` declaration.
- `admin/src/app/layout.tsx` wires `next-themes` with `defaultTheme="system" enableSystem`.
- `admin/src/components/app-sidebar.tsx` has a "Switch to light theme / Switch to dark theme"
  item in the account menu.
- `admin/src/components/patterns/status-badge.tsx` has a `dark:` variant in its warning tone.

So a signed-in user whose OS is set to dark is in dark mode right now, without touching anything.
Replacing only `:root` leaves those users on Yeti charcoal with the old teal primary while everyone
else moves to indigo, and the account menu keeps offering the switch. Deferring dark mode is
therefore not a no-op, it is a decision to either ship a mismatched second theme or remove a feature
people already have. Three options, in order of cost:

1. Draw the dark token set now. The README says the palette inverts cleanly. Cost is one design
   pass and a second axe run.
2. Map the `.dark` block onto the new tokens as a rough inversion, accept that it is not designed,
   keep the toggle.
3. Remove the toggle, force light, drop `next-themes` from the layout. Cheapest to build, and it
   takes a working feature away from every existing deployment. `CLAUDE.md` section 3 calls that
   shape of change out by name: a default that silently removes a feature from every existing
   deployment is a breaking change with nothing in the signature to show for it.

This is the decision that blocks the token swap, and the token swap blocks everything else.

### The base layer fights the new type scale

`globals.css` ends with:

```css
h1, h2, h3, h4, h5, h6 { font-weight: 300; }
```

Sora 600 will not apply to any heading until that rule goes. It is two lines and easy to miss.

### Font variables are misnamed

`admin/src/app/layout.tsx` loads Open Sans into a variable called `--font-geist-sans` and Geist Mono
into `--font-geist-mono`, and `globals.css` maps `--font-sans`, `--font-mono` and `--font-display`
onto those names. The swap to Sora, Manrope and JetBrains Mono touches both files, and the variable
names should be renamed while they are open, or the next reader will look for Geist.

### The accessibility gate is real and it has bitten twice

`globals.css` documents two prior WCAG remediations, both caught by the axe pass. `admin/package.json`
carries `@axe-core/playwright`. Every token pair in the new palette has to clear it, including the
tinted-panel pairs the README lists without contrast ratios (`accent-ink` on `accent-soft`,
`accent-deep` on `accent-soft`, and the three status pairs). The README verifies `muted` and stops.

### The monospace rule adds a third family

"Every machine-produced value is monospace" is the rule that does most of the visual work, and it
means three Google font families where there are two today. Worth a bundle check before it lands,
not after.

---

## 1. Site landing (`site/app/page.tsx`)

**Component.** No admin token benefit at all: `site/` has its own `site/app/globals.css` and its own
layout, so the admin theme swap does nothing here. This screen is a separate piece of work that
happens to share a palette.

What exists: `site/app/page.tsx`, `site/app/layout.tsx`, `site/app/bean.tsx` (the bean geometry the
README says is unchanged apart from the fill), `site/app/ledger.tsx`, and the Umbraco comparison
table the design restyles rather than rewrites.

What does not exist: the product shot. The README calls the browser frame showing the real entry
editor "the single most important element on the page". There is no such asset, and the design
bundle ships none (its own note says imagery is absent and media thumbnails are placeholders).
Producing it means either a real screenshot of the redesigned entry editor, which cannot be taken
until screen 5 is built, or a hand-built static mock that will drift. Sequencing consequence: the
landing page's most important element depends on the most expensive admin screen.

The name stays BarakoCMS here. See #347.

## 2. Admin sign in (`admin/src/app/login/page.tsx`)

**Component, plus one data change, plus one item with no backend.**

The current page (231 lines) already handles three states: password, TOTP challenge, and emailed
device-approval code. It has the password reveal toggle (`IconEye` / `IconEyeSlash`) and the real
lockout copy the design quotes ("After 5 failed tries the account locks for 15 minutes"). So the
card, the bean, the heading and the layout are a restyle.

The two buttons under the `OR` divider are not all free:

- **"Email me a sign-in code"** is real. `barakoCMS/Features/Auth/Otp/RequestEndpoint.cs` serves
  `POST /api/auth/otp/request`, and `useVerifyDeviceCode` in `admin/src/hooks/use-auth.ts` already
  handles the verify half. Missing is a request hook and the entry-point wiring. Small.
- **"Continue with GitHub"** is real but conditional. `BarakoCMS.ExternalAuth/GitHubAuthEndpoints.cs`
  exists, and so does `BarakoCMS.ExternalAuth/AuthProvidersEndpoint.cs`, which is exactly the
  endpoint you need to ask which providers a deployment has configured. The admin never calls it.
  ExternalAuth is an optional module, so rendering the button unconditionally shows a dead control on
  every deployment that does not install it. This is the data change: a `use-auth-providers` hook and
  a conditional render.
- **The "Forgot?" link has nothing behind it.** `barakoCMS/Features/Auth/` contains Login, Logout,
  Mfa, Otp, Refresh and Register. A repo-wide grep for `forgot-password`, `reset-password` and
  `ForgotPassword` returns nothing. Shipping the link means shipping a password-reset feature, which
  is a security-surface feature with its own threat model, not a link. Either build it as separate
  work or drop the link from the design.

## 3. Admin Overview (`admin/src/app/(admin)/page.tsx`)

**Route or data, and the most expensive screen in the set.** The layout is a rewrite, which is
expected. The problem is that most of what the new layout displays has no source.

### The event stream collides with an open decision on the same milestone

The design's Overview rail renders an event feed: `ContentStatusChanged`, `FieldUpdated`,
`JournalEntryPosted`, `UserSignedIn`, `ContentCreated`, each with a timestamp. The README calls this
"the product's actual differentiator" and the reason the redesign leads with it.

Open issue **#229**, milestone 4.0.0, decides the opposite: "The stream is internal. History is
exposed only as a projected, versioned view", and its Done-when adds a test that fails if any type
under `Features/**` references `barakoCMS.Events.*`. Those five strings in the design are event type
names. There is no endpoint that returns them, and #229 exists specifically to stop one being added.

There is also no cross-stream feed of any kind. `useContentHistory(id)` in
`admin/src/hooks/use-contents.ts` is per-entry and returns `ContentVersion`, whose `changeType` is,
per `admin/src/types/content.ts`, "Decided server side, not the event class name". That is #229's
design already in force.

This is resolvable, but it is a decision, not a ticket: define a projected, named activity feed with
its own stable vocabulary and a new endpoint, and accept that the labels on screen are that
vocabulary rather than the event types. That is server work plus a naming exercise, and it has to
happen before this panel can be drawn.

### The four stat cards

| Card | Source today | Verdict |
| --- | --- | --- |
| Entries, 148 | `useContents().totalItems` | free |
| Published, 121, with "27 draft, 3 scheduled" | nothing | data change |
| Delivery API 24h, 42.9k, "cache hit 91%" | nothing | data change |
| Visitors 7d + sparkline | Analytics.Umami module | conditional |

`useContents` accepts `PageParams & { contentType?: string }` and nothing else, so there is no way to
count published, draft or scheduled entries without paging the whole collection. `MetricsSummary` in
`admin/src/hooks/use-monitoring.ts` is `{ totalRequests, totalErrors, averageResponseTime, errorRate }`,
all cumulative since process start: no 24h window, no cache-hit rate, and no p95, which the header
pill also asks for (`p95 38ms · err 0.04%`). The System card additionally wants uptime, also absent.
The Visitors card comes from the optional `BarakoCMS.Analytics.Umami` module, so the four-card row
becomes three on a deployment without it, and the design has no three-card state.

### "Needs you"

The README says this is "a derived view ... not a new endpoint". It is derived, but not from what
exists. It needs scheduled entries (`scheduledPublishAt` is on `ContentDetail`, not on
`ContentListItem`), drafts past an age (no status filter, no age filter), and module-reported
problems, which is a mechanism that does not exist at all: nothing lets Accounting say "this journal
entry is unbalanced" or Email events say "two bounces" into a shared list. Replacing "Latest
entries" with "Needs you" is the right call and it is the single most expensive idea in the handoff.

## 4. Entries (`admin/src/app/(admin)/content/page.tsx`)

**Route or data.** The table itself is a restyle. The filter bar is not.

- **Search, "Search 148 entries".** `GET /api/contents` takes page, pageSize and contentType. No
  query parameter. Server change.
- **Status segmented control, All / Published / Draft / Scheduled / Archived.** No status parameter.
  Worse, `ContentStatus` in `admin/src/types/content.ts` is `Draft | Published | Archived`.
  **Scheduled is not a status**, it is derived from `scheduledPublishAt`, which the list item does
  not carry. So this control needs both a new filter parameter and a decision about whether
  Scheduled becomes a real status or a derived one the server can filter on.
- **The `V` column.** `ContentListItem` has `id, contentType, data, createdAt, updatedAt, status,
  sensitivity`. No `version`. Only `ContentDetail` has it. Server change to the list response.
- **The `Private` pill with a lock.** Free. Join `useSchemas()` on `isPubliclyDeliverable` client
  side.
- **`Unbalanced` and `Posted` pills.** These appear in `screenshots/03-admin-entries.png` in the
  status column, on `journal-entry` rows. They are not `ContentStatus` values, they are Accounting
  concepts, and there is no mechanism for a module to contribute a row state to the generic entries
  table. Either the design's status column is showing something the platform does not model, or this
  is a new module-contributed-state feature. The README does not mention it; it says only to add the
  `Private` pill.

Positive note: `statusMeta()` already refuses to invent a status the server did not send, and
renders the raw value instead. Any new status flows through it without a code change.

## 5. Entry editor (`admin/src/app/(admin)/content/[id]/page.tsx`)

**Component, plus one tab with nothing behind it.** 391 lines today, and most of the design's
substance is already there.

Free or component-level:

- Tabs. Today they are Edit / Schedule / History. The design's Content / Scheduling / History is a
  rename plus a pill restyle.
- The event-stream rail with version cards and "Restore this version". `useContentHistory` and
  `useRollbackContent` both exist, the restore confirmation copy exists, and `ContentVersion` carries
  `changeType`, `lastModifiedBy` and `timestamp`. One caution: history is fetched today only while
  the History tab is open (`enabled: active`). Promoting it to an always-visible rail means it
  fetches on every editor open.
- The mono meta line (`article · v7 · publishes today 09:00 · /api/public/article/spring-roast`).
  Every part is on `ContentDetail` plus the schema.
- Field type hints (`string · required`, `markdown`). On `FieldDefinition` already.
- The accent-tinted `Publish at` when a schedule is armed. `SchedulePanel` already knows.

Not free:

- **The Permissions tab.** There is no per-entry permission model. Sensitivity exists in two places:
  on the entry (`ContentDetail.sensitivity`) and per field on the content type
  (`FieldDefinition.sensitivity`, `visibleToRoles`, `mask`). Per-field sensitivity cannot be changed
  after the type is created, which is open issue **#163**. So a Permissions tab is either read-only,
  showing what the type declares and why the value is masked, or it is the UI for #163 plus a new
  per-entry model. The README promotes it out of the field list as "a deliberate change" without
  saying which.

Also worth noting: the design's "Restore this version" appears on every older card. The current code
gates restore on `canRollback` (SuperAdmin or Admin) and on the version actually carrying a document,
since status changes and schedule changes appear in history but cannot be restored to. Both gates
must survive the redesign.

## 6. Content type builder (`admin/src/app/(admin)/schemas/new/page.tsx`)

**Component.** The closest of the admin screens to already matching, apart from one dependency.

Already there, in `schemas/new/page.tsx` and `admin/src/components/schema/field-editor.tsx` (366
lines): display name beside API name with the slug auto-derived from the display name, the
public-delivery toggle whose description shows the live `/api/public/{name}` route, the seventeen
field types in `FIELD_TYPES`, required, sensitivity, and the four masks in `FIELD_MASKS`.

The two real changes:

- **Draggable field rows.** No drag-and-drop library is in `admin/package.json`. This is a new
  dependency (`@dnd-kit/*` or similar) plus keyboard-accessible reordering, which the axe gate will
  ask about. Note also that field order is currently just array order in `CreateSchemaRequest`, so
  the reorder itself is free once the interaction exists.
- **The expanded-mid-edit field row.** `field-editor.tsx` edits a field in a `Dialog` today. The
  design edits it inline in the list. That is a restructure of the component, not a restyle, and it
  is the right one: the README is correct that per-field sensitivity is worth making visible, and a
  dialog hides it.

The design shows the builder route only. `admin/src/app/(admin)/schemas/page.tsx` (the list) and
`schemas/[name]/page.tsx` (the detail) are not redesigned and will inherit the theme.

## 7. Workflows (`admin/src/app/(admin)/workflows/page.tsx`, `workflows/new/page.tsx`)

**Route or data.** The README asserts "Everything here follows `types/workflow.ts`". For the trigger
events and the six action types that is exactly right. For the workflow cards it is not.

- **The enable toggle does not exist and cannot be built as drawn.** `WorkflowDefinition` in
  `admin/src/types/workflow.ts` is `{ id, name, triggerContentType, triggerEvent, conditions,
  actions }`. There is no enabled or paused field. And `admin/src/hooks/use-workflows.ts` exposes
  read, create, validate and dry-run: **there is no update mutation and no delete mutation at all.**
  So the toggle has no state to read and no endpoint to write. "Paused workflows drop to 0.72
  opacity" describes a state the system does not have. This needs a model field, an endpoint, the
  engine honouring it, and a test, before any of it is drawn.
- **The run-health line per card.** `useWorkflowDebugLogs(id)` hits `/api/workflows/{id}/debug`, one
  workflow at a time. A list of N cards each showing run health is N requests on load, or a new
  aggregate endpoint. Related to open issue **#329**, which moves workflow execution out of the
  projection and records every attempt. Sequence this after #329 rather than building a health line
  on top of the current debug log.
- **The builder.** All the parts are in `workflows/new/page.tsx` (308 lines): trigger content type,
  trigger event limited to Created and Updated, condition rows, an ordered action list with
  per-action parameters from `useWorkflowActions()`, and the template variable palette scoped to the
  trigger type via `useWorkflowVariables(contentType)`. The design arranges them on a vertical
  timeline with three nodes. That is a component change and a good one.
- **"Last runs" belongs on a different route.** The design puts a run log under the builder. The
  builder route in the design map is `workflows/new`, which by definition has no runs. The log
  belongs on `admin/src/app/(admin)/workflows/[id]/page.tsx`, which exists and is not in the design.

The `{{variable}}` escaping note in the README applies only to the prototypes and can be ignored in
React, as the README says.

## 8. Analytics (`admin/src/app/(admin)/analytics/page.tsx`)

**Component, and the cheapest of the nine.** 363 lines that already implement nearly the whole
design.

Present today: the website picker, the range picker over `24h | 7d | 30d | 90d`, four summary cards
with a previous-period delta computed by `delta()` and coloured by direction, the trend chart via
`admin/src/components/analytics/sparkline.tsx`, and all six breakdown cards in the design's order and
grouping (Top pages in mono, Referrers with a "Direct visits only" empty state, Countries with a
`flag()` helper, Devices, Operating systems, Browsers), each a proportional bar with a right-aligned
count. Live status comes from `useSiteStatus`, which returns `activeNow` for the "active now" pill.

Two changes:

- The range picker becomes a segmented control. Entries needs the same control for its status
  filter, so build it once as a shared primitive rather than twice.
- **The dashed previous-period line on the trend chart is not fetchable.** `AnalyticsSeries` returns
  one `pageviews` series and one `sessions` series for the requested range, and `AnalyticsRange` is a
  closed union of four values with no from/to. There is no way to ask for the window before the one
  you are showing. This is a small module change in `BarakoCMS.Analytics.Umami`, but it is a server
  change, and it is the only one on this screen.

The sparkline is dependency-free hand-rolled SVG. Two lines, one filled and one dashed, is a modest
extension of it, not a charting library.

## 9. Tenant switcher (`admin/src/components/tenant-switcher.tsx`)

**Component, plus a data change, plus a relocation.**

Today it is a shadcn `Select`, 76 lines, rendered in `admin/src/components/app-header.tsx`. The
design is a 330px popover anchored under a button in the sidebar. So this moves as well as changes
shape.

- **`slug · role` per tenant is not available.** `TenantSummary` in `admin/src/hooks/use-tenants.ts`
  is `{ slug, name, logoUrl, branding }`. The caller's role in each tenant is not returned by
  `/api/me/tenants`. Adding it is a small server change and worth doing, because the design's own
  footer sentence ("your role can differ per tenant") is only meaningful if the list shows the role.
- The find field is component-level. The check on the current tenant is free (`useCurrentTenant()`).
- **Home** for the default partition already exists, and the comment in `tenant-switcher.tsx`
  explains why it must: without it the default partition becomes unreachable once you switch away,
  which has already stranded a deployment's data once.
- The footer sentence about re-issuing the token is accurate: `useSwitchTenant` posts to
  `/api/me/switch` and calls `queryClient.invalidateQueries()` with no key, dropping everything.
- **"Create a tenant"** needs a role gate. `useCreateTenant` exists but `/api/tenants` is SuperAdmin
  only, so the footer link has to hide for everyone else.
- Do not lose the auto-switch behaviour. The current component switches into the user's first tenant
  when the token is not scoped to one they belong to, once per mount, and a manual pick sets the same
  ref so a deliberate switch to Home sticks. That logic is subtle, it is commented, and a rewrite
  will drop it if nobody is looking for it.

## Sidebar (`admin/src/components/app-sidebar.tsx`, `admin/src/lib/navigation.ts`)

**Component, plus two data gaps, and the README's premise is out of date.**

The README says the current sidebar is "19 flat items" and calls grouping "the main fix to 'not
intuitive'". It is not flat. `admin/src/lib/navigation.ts` already defines `NAV_GROUPS` with an
ungrouped Overview followed by labelled **Content**, **Access**, **Modules** and **System** groups,
and `visibleGroups()` filters items by role and drops a group whose items are all filtered out. The
comment in `app-sidebar.tsx` records that the flat nineteen-item version is the thing that was
already fixed. `navigation.test.ts` covers it.

So the grouping is done. What is actually left:

| Design element | Status |
| --- | --- |
| Group labels, uppercase, small | Exists, restyle only |
| Role filtering | Exists, keep it |
| Mono counts on Entries, Content types, Workflows | Three list queries on every page |
| "Modules, 5 installed" | Needs #185, no `/api/meta/modules` exists |
| Email events badge, warning tint | No unread concept exists |
| Errors badge, danger tint | Free, `useClientErrors({ resolved: false }).totalItems` |
| Search button with `⌘K` kbd | The menu exists in `command-menu.tsx`, the trigger button does not |
| Tenant switcher in the rail | Moves out of `app-header.tsx`, see screen 9 |
| Account card in the footer | Exists, restyle only |
| Version in mono beside the wordmark | Exists, `useApiMeta().version`, currently in the footer |
| "Add a module" accent row | New, and it has no destination |
| Active item as a white card on the page background | Theme plus the rail treatment |

Three things the table does not capture:

- **"Users and roles" is one nav item in the design.** Today `/users` and `/roles` are two routes and
  two pages (`admin/src/app/(admin)/users/page.tsx`, `roles/page.tsx`, plus `roles/new` and
  `roles/[id]`). Merging them in the nav means either one of them becomes unreachable from the
  sidebar or the two pages merge. That is a route change, and the merged page is not designed.
- **The design has no breadcrumb bar.** `app-header.tsx` renders breadcrumbs from
  `breadcrumbsFor()`, which has its own tests in `navigation.test.ts`. The new header is page title,
  health pill, a bolt button and the primary action. Deleting the breadcrumbs means deleting
  `breadcrumbsFor`, `SEGMENT_TITLES` and their tests, or keeping dead code.
- **The design has no collapsed state.** The shadcn `Sidebar` is `collapsible="icon"` and
  `SIDEBAR_WIDTH` is `16rem` (256px). The design is 248px with a padded rail and no collapse
  affordance drawn, and several components carry
  `group-data-[collapsible=icon]:hidden` classes that assume one. Decide whether collapse survives
  before rebuilding, because the answer changes the markup.

Counts are the sneaky cost. Putting live counts for Entries, Content types and Workflows in the
sidebar means those three queries run on every route in the admin, not just on their own pages.
TanStack Query will cache them, but the first paint of every session pays for all three.

---

## Screens not in the redesign

The README's third open question lists them. Concretely, these routes exist and are not designed:

`accounting`, `api-keys`, `audit`, `content/new`, `email-events`, `errors`, `feature-flags`,
`ops/health`, `pwa`, `roles`, `roles/new`, `roles/[id]`, `schemas`, `schemas/[name]`, `settings`,
`settings/security`, `tenants`, `user-groups`, `users`, `workflows/[id]`.

That is twenty routes against nine designed screens, not "roughly ten". They all inherit the tokens,
so none of them will look broken, but every one keeps the current layout: `PageHeader` above a
bordered table, no rail, no panel, no mono metrics. The seams show most where a designed screen links
straight into an undesigned one: Entries to `content/new`, the content type builder to `schemas`,
Workflows to `workflows/[id]`.

The shared primitives are what make this survivable. `admin/src/components/patterns/` holds
`page-header`, `status-badge`, `empty-state`, `error-state`, `table-skeleton`, `pagination-controls`
and `confirm-dialog`, and every undesigned route uses them. Restyling those seven files moves all
twenty routes a long way toward the new language without designing any of them. That is worth doing
as part of the theme work, not after it.
