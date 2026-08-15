# From packages to a marketplace

The modules are already published, tagged and discoverable. A marketplace is a page that reads NuGet,
not a registry to build. This is the order to do it in, and what has to be true first.

*Figures verified against the NuGet v3 API, the local test suites and the live deployments on
2026-08-15.*

---

## Where this actually stands

Every barakoCMS package now ships an icon, a README, the `barakocms-module` tag, XML documentation and
Source Link with symbols. That was the missing groundwork: a package with a blank page and no tag
cannot be listed by anything.

The listing mechanism is already proven in production by Umbraco. Their marketplace is not a registry
people submit to — it queries NuGet for a tag:

| Query against NuGet search | Result | |
|---|---|---|
| `tags:umbraco-marketplace` | 968 packages | works today |
| `tags:barakocms-module` | 0 packages | until released |

Ours returns zero for one reason only: the tagged versions are merged to `master` but not yet
published. The moment a release runs, fourteen packages appear — with icons, download counts and
descriptions — and so does anything anyone else publishes carrying the same tag.

> **The consequence worth naming.** A marketplace built this way has **no submission process and no
> gatekeeper**. That is its main strength and its main risk, and both are addressed in Phase 3.

---

## Phase 1 — Make the core worth building on

*Nothing else matters if a module author cannot trust the surface underneath them.*

A marketplace is a promise that these modules keep working. Two things currently undermine that, and
both should land before inviting anyone to build against the core.

### Public delivery must be opt-in

`GET /api/public/{type}` is anonymous and serves *any* content type whose content is Published with
Public sensitivity — and both are the defaults. A host modelling members or orders as content gets a
public endpoint for them without ever asking.

This is not theoretical. On a live deployment it exposed a member roster and a chart of accounts to
anonymous callers; that deployment was blocked at nginx as a stopgap. The real fix is an explicit
per-content-type flag, defaulting to off. Tracked in [#81](https://github.com/BaryoDev/barakoCMS/issues/81).

> **Breaking change — ship it deliberately.** baryo.dev serves its blog through this same endpoint,
> entirely legitimately. So the change needs a migration that sets the flag on types already being
> delivered, plus an unmissable upgrade note. Getting this wrong takes live sites dark.

### Close the token window

Revoking refresh tokens does not invalidate an access token already issued, so enabling MFA, changing
a password, or signing out everywhere all leave a stolen session working for up to fifteen minutes. A
per-user `TokensValidFrom` timestamp closes it — but it runs on every authenticated request, so a
mistake locks everybody out at once. Tracked in [#82](https://github.com/BaryoDev/barakoCMS/issues/82).

### Then declare the module contract stable

Once those land, write down what a module may depend on and what it may not, and version it. Today the
contract is implicit — it is whatever `IBarakoModule` happens to expose. An author needs to know which
parts will still be there in a year.

---

## Phase 2 — Make modules easy to improve

*Already partly done. The remaining work is lowering the cost of a first contribution.*

Issue templates, Discussions, a Discord link, and a CONTRIBUTING guide covering branch naming, the
failing-test rule, module publishing and design contributions are all in place. Five issues are filed
so an arriving contributor has something concrete to take.

### What is still missing

| Gap | Why it blocks contribution | Tracked |
|---|---|---|
| Module template | Writing a module means copying one and deleting parts. A `dotnet new` template encodes the conventions and sets the tag by default, so third-party modules are discoverable without the author knowing to do it. | [#84](https://github.com/BaryoDev/barakoCMS/issues/84) |
| Per-module icons | Every package shares the bean, so they are visually identical on nuget.org. Also the most approachable first contribution, and open to non-programmers. | [#80](https://github.com/BaryoDev/barakoCMS/issues/80) |
| xunit v3 | Holds the FastEndpoints line back, which pins a dependency contributors will hit. | [#83](https://github.com/BaryoDev/barakoCMS/issues/83) |
| Version-bump friction | The module-version guard fires on a README-only change, because a README *is* package content. Correct, but surprising — it needs to be in CONTRIBUTING, not learned from a red build. | — |

> **Keep the guardrails; they are the reason this can open up.** Packaging is checked by tests rather
> than convention: 45 assertions fail if a package lacks a README, re-pins shared metadata, or ships an
> icon that is not a real PNG under NuGet's size limit. That last one otherwise fails at push time,
> long after CI is green. A convention nothing enforces decays the moment strangers start contributing.

---

## Phase 3 — barakocms.baryo.dev

*A landing site whose most useful page is the module list.*

The site has three jobs: explain what barakoCMS is, get someone to a working install, and list the
modules. Nothing about it needs a database.

### How the marketplace works

Resolve the search endpoint from the service index — never hardcode it, it moves — then query the tag
and render the results. That is the whole backend.

```
// 1. discover the current search service
GET https://api.nuget.org/v3/index.json
    -> resources[] where @type starts with "SearchQueryService"

// 2. query the tag
GET {searchService}?q=tags:barakocms-module&take=100&prerelease=false

// each result already carries what a card needs:
//   id · version · description · iconUrl · totalDownloads
//   authors · projectUrl · tags
```

Static generation with hourly revalidation. If NuGet is unreachable, serve the last good payload rather
than an empty page — a marketplace that intermittently shows nothing reads as a dead project.

### Trust, without a submission queue

Anyone can publish a package carrying our tag, including someone hostile. The answer is not to add a
gatekeeper — that kills the thing that makes this work — but to be explicit about provenance:

- **Official** — published by BaryoDev. Verified against the package *owner*, not the authors field,
  which anyone can set to anything.
- **Community** — everything else, listed plainly, with no implied endorsement.
- **Featured** — a short curated list in the repo. A pull request to be added is the entire process,
  and it is reviewed like any other.
- **Removed** — a denylist, also in the repo, for anything abusive. Public, so a removal is on the
  record rather than silent.

Sort by downloads by default. It is the one signal that is honest and that nobody controls.

### Hosting

Same Oracle VM as the other sites, behind nginx with its own certificate, deployed the way playground
already is. No new infrastructure.

---

## Phase 4 — BaryoVM, documented like something you would adopt

*Twenty-one commands, one release, zero stars, no contributing guide.*

We dogfood it — three stacks are registered and it deploys the sites in this plan — but nothing about
the repository invites anyone else in. It has a README, a USAGE guide and a VISION doc, which is
genuinely more than most tools this age have. What it lacks is everything that turns a reader into a
contributor.

### Test coverage, honestly stated

The parts that survived real incidents are well covered. The parts nobody has been burned by yet are
not covered at all.

| Package | Coverage | Note |
|---|---|---|
| `internal/release` | 89.7% | — |
| `internal/update` | 70.9% | health-gate and rollback, written after a live crash-loop |
| `internal/fleet` | 40.0% | round-trips the settings that silently dropped `--sudo` |
| `internal/compose` | 26.0% | regression cover for five live-only bugs |
| `internal/sshx` | 2.7% | the layer everything else runs through |
| `internal/cli` | 0% | flag wiring — where `--sudo` was lost |
| `internal/deploy`, `backup`, `bootstrap`, `provider`, `toolchain`, `ui` | 0% | — |

`internal/cli` at zero is the pointed one: a flag was bound but never assigned, so it read as false,
updates ran unelevated, and the failure looked like a host permissions problem. Go does not catch it,
because the variable *is* used — by the flag binding.

### What to add

- **CONTRIBUTING and a code of conduct.** Both missing. Mirror the barakoCMS ones so the two projects
  feel like one house.
- **A command reference.** Twenty-one commands, documented by prose. Generate it from cobra so it
  cannot drift.
- **An honest scope statement.** It deploys to VMs you already own, over SSH, agentlessly. Saying
  plainly what it is *not* saves everyone's time.
- **A five-minute quickstart** that ends with something actually deployed. The current USAGE guide
  assumes a fleet already exists.
- **Golden-file tests for command construction**, so contributors can change shell-out behaviour
  without a VM. This is what makes the 0% packages contributable at all.
- **Prebuilt binaries per release.** One release exists and it is source-only, so trying it means
  having Go installed.

> **Sequencing.** This sits last on purpose. BaryoVM serves barakoCMS; a deploy tool with no adopters
> is a smaller problem than a CMS whose module authors cannot rely on the core.

---

## Decisions this needs

Everything else can proceed without asking.

- **Release.** Cutting a release is what puts the fourteen tagged packages on nuget.org and makes the
  marketplace non-empty. Until then Phase 3 has nothing to render.
- **Breaking.** Whether public delivery becomes opt-in now — with a migration for sites already relying
  on it — or waits for a major version. This gates Phase 1.
- **Scope.** Whether barakocms.baryo.dev absorbs the documentation currently in the repo, or only links
  to it. Absorbing it is better for readers and more work to keep honest.
- **Naming.** Whether the coffee-menu branding is public-facing on the landing page or stays an
  internal shorthand. It is genuinely distinctive; it is also unexplained to a newcomer.
