repo: BaryoDev/barakoCMS
branch: master

## Last sync

date: 2026-09-01T01:10:51Z

### Updated in this project

- Recreated the current admin and the barakocms.com landing page from source, with the repo's own Line Awesome icons vendored as an SVG sprite.
- Explored three 2026 redesign directions; Signal (indigo, Sora + Manrope, 14px radius) was chosen.
- Built Signal across nine screens: landing, sign in, Overview, Entries, entry editor, content type builder, Workflows, Analytics, tenant switcher.
- Admin product renamed to barakoBrew in the admin chrome; the marketing site stays BarakoCMS.

## Screen map

| Project screen | Repo files |
| --- | --- |
| Current State — Admin Overview | `admin/src/app/(admin)/page.tsx`, `admin/src/app/(admin)/layout.tsx`, `admin/src/components/app-sidebar.tsx`, `admin/src/components/app-header.tsx`, `admin/src/components/brand.tsx`, `admin/src/lib/navigation.ts`, `admin/src/components/analytics/overview-analytics.tsx`, `admin/src/components/analytics/sparkline.tsx`, `admin/src/components/patterns/page-header.tsx`, `admin/src/components/patterns/status-badge.tsx`, `admin/src/components/ui/{card,button,badge,sidebar,select,breadcrumb}.tsx`, `admin/src/app/globals.css`, `admin/src/app/layout.tsx` |
| Current State — Entries | `admin/src/app/(admin)/content/page.tsx`, `admin/src/components/ui/table.tsx`, `admin/src/components/patterns/pagination-controls.tsx` |
| Current State — Entry editor | `admin/src/app/(admin)/content/[id]/page.tsx`, `admin/src/components/content/dynamic-form.tsx`, `admin/src/components/ui/{tabs,switch,input,textarea}.tsx` |
| Current State — Content types | `admin/src/app/(admin)/schemas/page.tsx`, `admin/src/app/(admin)/schemas/new/page.tsx`, `admin/src/components/patterns/empty-state.tsx` |
| Current State — Sign in | `admin/src/app/login/page.tsx` |
| Current State — Site landing | `site/app/page.tsx`, `site/app/layout.tsx`, `site/app/globals.css`, `site/app/ledger.tsx`, `site/app/bean.tsx` |
| Redesign Directions (1a/1b/1c) | Content and IA derived from the screens above; visual language is new |
| Signal — Workflows | `admin/src/app/(admin)/workflows/page.tsx`, `admin/src/app/(admin)/workflows/new/page.tsx`, `admin/src/types/workflow.ts`, `admin/src/components/workflow/action-icon.tsx` |
| Signal — Analytics | `admin/src/app/(admin)/analytics/page.tsx`, `admin/src/components/analytics/sparkline.tsx`, `admin/src/hooks/use-analytics.ts` |
| Signal — Tenant switcher | `admin/src/components/tenant-switcher.tsx`, `admin/src/hooks/use-tenants.ts` |
| Proposed — Modules, Agents (MCP), Media, Review, candidate primitives | `ROADMAP.md`, `README.md` (gap table, module-or-core tests, marketplace tag), `MODULES.md` contract as described in README |
| icon-sprite.js | `admin/src/components/icons/index.tsx` (Line Awesome by Icons8) |
