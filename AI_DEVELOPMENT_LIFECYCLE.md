# AI Development Lifecycle

How barakoCMS is built and shipped. The code is written with an AI pair (Claude Code), but the
process around it is deliberately engineered: nothing reaches users because a model felt confident.
Every change is proven locally, gated by CI, deployed to a breakable tier, and verified running
before it is promoted.

This doc is the **playbook**. If you (person or agent) are about to build the next feature, follow
the checklist in "Shipping a feature, step by step" and use the rest as reference.

## Principle: the AI writes, the process proves

An agent produces plausible code quickly. Plausible is not correct. The lifecycle closes that gap:

- **Tests gate every promotion.** Not "the model says it's fine" — the suite runs, red stops the line.
- **Prove it locally first.** Unit + integration + e2e run on your machine before the branch is
  pushed. CI is the backstop, not the first discovery.
- **A breakable tier absorbs mistakes.** New builds land on dev-playground first, on an empty
  database, where breaking things is the point. Only then do they touch anything users see.
- **Deploys are verified, not assumed.** After every deploy a smoke test logs in, creates content,
  and checks validation still rejects bad input. "It deployed" means the app worked, not that a job
  went green.

That last point is not hypothetical. `AutoCreate.None` looked correct in review and passed on every
existing environment — because they already had schema. Standing up dev-playground on an empty
database is what surfaced the boot crash (`relation "mt_doc_roles" does not exist`) before a real
user hit it. Writing the field-types e2e is what surfaced a dashboard crash on partial metrics data.
The process finds the bugs; that is the point of it.

## The environments

| Tier | URL | Purpose | Deploy trigger |
|---|---|---|---|
| **dev-playground** | dev-playground.baryo.dev | Breakable staging. Break it freely. | Push to `dev` |
| **playground** | playground.baryo.dev | Public demo. Released versions only. | Version-gated `master` release |
| **club** | club.baryo.dev | Real members. | By hand, on purpose |

The club is never in the automated pipeline — real people, separate blast radius. Do not wire it in.

## The loop

```mermaid
flowchart TD
    A[Work on a branch] --> Z[Test LOCALLY first:<br/>unit + integration + edge<br/>+ Playwright e2e, written<br/>with the feature]
    Z -- red --> A
    Z -- green --> B[Open PR]
    B --> C{CI: backend tests · admin lint/type/vitest<br/>· full e2e pack · security scan}
    C -- red --> A
    C -- green --> D[Merge to dev]
    D --> E[deploy-dev-playground.yml:<br/>test → arm64 images → forced-command<br/>deploy → verify 200 → smoke test]
    E -- red --> P[Discord ping]
    E -- green --> F[Break it by hand on dev-playground<br/>+ capture screenshots]
    F -- problem --> A
    F -- holds up --> G[Bump &lt;Version&gt; in barakoCMS.csproj]
    G --> H[PR dev → master, merge]
    H --> I{release.yml gate:<br/>version already on NuGet?}
    I -- yes --> J[No-op. Nothing ships.]
    I -- no --> K[Publish NuGet + GH Packages<br/>Docker amd64 + arm64 :playground<br/>Promote playground → smoke test<br/>Announce w/ screenshots]
    K -- red --> P
```

## Shipping a feature, step by step

A checklist. The field-types feature (F.1/F.2) is the worked example throughout.

### 0. Build it with its tests, and prove them locally

Write the feature and its tests together, then run everything on your machine. Nothing is pushed
until this is green.

- **Backend unit tests** for the logic, edge cases included — malformed input, boundaries, aliases,
  empty values. Example: `FieldTypeRegistryTests` checks each new type's format, the parity between
  validators, and JsonElement handling.
- **Backend integration tests** for the API path, against a real Postgres (Testcontainers) — no
  mocking your own layer. Example: `ValidationIntegrationTests` posts to the real `/api/content-types`
  and `/api/contents`, asserting a valid value is accepted (200) and a malformed one rejected (400).
- **Admin e2e (Playwright)** for anything with a UI, driving the real components with a mocked API.
  Example: `field-types.spec.ts` asserts each type renders the right control, a valid entry saves,
  and a bad value surfaces the server error.

```bash
# backend (unit + integration; Testcontainers needs Docker running)
dotnet test BarakoCMS.Tests/BarakoCMS.Tests.csproj -c Release

# admin
cd admin && npm run lint && npx tsc --noEmit && npx vitest run
npx playwright test                 # full pack, all viewports
```

Rule of thumb: **whatever you'll later verify by hand on dev-playground, pin it in a test first.**

### 1. Branch, PR, CI

Work on a branch off `dev`. Push it and open a PR. CI (`ci.yml`) runs on every push (except master)
and every PR:

- **Backend** — build + full `dotnet test` (Testcontainers Postgres).
- **Admin** — lint, typecheck, vitest, production build, and the **whole e2e folder** on chromium.
- **Security** — gitleaks secret scan + a vulnerable-dependency report (both report-only for now;
  see the security note below).

Red blocks the merge (the security job is informational until its backlogs are cleared). It is the same gate for a person or an agent. These are the same tests that
already passed locally — CI confirms, it does not discover.

### 2. Merge to `dev` → dev-playground

Merging to `dev` triggers `deploy-dev-playground.yml`:

1. Run the test suite again (a merge is not a PR).
2. Build `:dev` suite + admin images natively on an arm64 runner (the Ampere VM is arm64; no QEMU).
3. Deploy over SSH with a **forced-command key** — the key in `authorized_keys` can only run
   `/home/opc/deploy-dev-playground.sh`, nothing else, so a leaked key can't open a shell. The script
   pulls both images, recreates the stack, and fails unless API and admin answer 200.
4. **Smoke test** (`scripts/smoke-test.sh`, write tier): log in, create a content type, post a valid
   value and a malformed one, confirm validation still rejects the bad one. A 200 means "up"; the
   smoke means "actually works."
5. If any of this fails, Discord gets pinged.

Then break it by hand on dev-playground. This is the tier where a broken build is fine.

### 3. Verify + capture screenshots

Confirm the feature does what you claimed, on the live tier. Capture screenshots for the
announcement while you're there:

```bash
cd admin && npx playwright test screenshots.spec.ts --project=chromium
# → admin/test-results/screenshots/*.png
```

### 4. Bump the version → PR to `master` → release

The single source of truth for a release is `<Version>` in `barakoCMS/barakoCMS.csproj`:

- **Bump it** in the PR — merging to master publishes that version and promotes it to playground.
- **Leave it unchanged** — the master merge is a no-op; the gate sees the version is already on
  NuGet and stops.

No auto-bumping. A merge never publishes by surprise, and a published version's Docker tags are never
overwritten with different bits. **To ship, bump the version.** Update `CHANGELOG.md` in the same PR.

Open the PR from `dev` to `master` and merge it with a **merge commit** (not squash — `dev` is
long-lived; see Branch model). When the version is new, `release.yml`:

1. **Gate** — read the version, check NuGet, decide if there's anything to release.
2. **Test** — the suite, once more.
3. **Publish** — core + 11 modules to NuGet.org and GitHub Packages; Docker images
   (`barako-cms`, `barako-cms-decaf`, `barako-admin`) as amd64 for public users, mirrored to Docker Hub.
4. **Build arm64 `:playground` images** on an arm64 runner, so the VM runs them natively.
5. **Deploy** the full stack to playground via forced command, verify 200, then a read-only smoke
   test (no writes on the public demo).
6. **Announce** to org discussions + Discord, with screenshots when there's something visual.
7. If anything fails, Discord gets pinged.

## What CI runs (`ci.yml`)

- **backend**: `dotnet build` + `dotnet test` (unit + integration, real Postgres).
- **admin**: lint, `tsc --noEmit`, vitest, `next build`, and `playwright test --project=chromium`
  over the **whole** `e2e/` folder — every feature spec is enforced, not honour-checked.
- **security**: gitleaks secret scan + `dotnet list package --vulnerable`. Both report-only for now —
  dev-only secrets still live in git history (roadmap 0.4) and there's a dependency backlog to burn
  down. Flip gitleaks to a hard gate once history is scrubbed.

## Post-deploy smoke test (`scripts/smoke-test.sh`)

Runs after every deploy. Tiers, each gated on the previous:

1. Always — `/health` and `/api/content-types` return 200 (app up, DB reachable).
2. With `SMOKE_USER`/`SMOKE_PASS` — login returns a token (auth works).
3. With `SMOKE_WRITE=1` — create a content type with an email field, post a valid entry (200) and a
   malformed one (400). **Write tier only where test data is fine (dev-playground), never the public
   demo.** The release runs the read-only tiers against playground.

```bash
SMOKE_USER=dev_admin SMOKE_PASS=… SMOKE_WRITE=1 \
  bash scripts/smoke-test.sh https://dev-playground.baryo.dev/barakocms-api
```

## Rollback

Every release pushes immutable `:playground-<version>` images. To roll back, run the **Rollback
playground** workflow (`rollback-playground.yml`) with the target version — it repoints the moving
`:playground` tag at that version (no rebuild), redeploys, and smoke-tests. Boring and fast.

NuGet versions are immutable and can't be cleanly unpublished — which is *why* publishing is gated
behind a deliberate version bump. The fix for a bad package release is a new, higher version.

## Branch model

- `dev` is **long-lived**. Feature branches merge into it; it auto-deploys to dev-playground.
- Release = a `dev → master` PR merged with a **merge commit**, then sync `dev` back so the next
  cycle starts aligned:

  ```bash
  git checkout master && git pull
  git checkout dev && git merge master && git push   # keep dev == master
  ```

- Never squash `dev → master` (it diverges the two branches). Squash is fine for a feature branch
  into `dev`.

## Where the human decides

The pipeline is automated; the judgment is not. A person, not the agent, decides:

- **When to cut a release** — by bumping the version. Publishing to NuGet is irreversible and
  outward-facing, so it is deliberate, never a side effect of merging.
- **What "stable enough" means** on dev-playground before promotion.
- **Anything touching the club.**

The agent's job is to make each of those cheap and safe to act on: fast local feedback, honest
verification, a breakable tier, and a one-button rollback.
