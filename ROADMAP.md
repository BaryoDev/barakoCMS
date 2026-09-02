# Roadmap

Releases ship every Saturday. This file says what each one is for and why that order.

The positioning it serves: **reachable, and free at any scale**. Not "cheaper than", not "has more
features than", and deliberately not "enterprise-ready", which is a claim about being a vendor rather
than about software.

Reachable first, because that is the part nothing else in the field offers: no sales call, no partner
tier, no procurement cycle, no discovery call before you can run it. Clone it and it works.

Free second, and precisely: no seat cap, no revenue cap, no metered AI, and every module in this
repository included.

That last phrase is deliberate and worth keeping precise, because two different things get called
"selling modules" and only one of them breaks the promise.

- **Every module in this repository is free, forever.** Nothing here gets moved behind a licence
  later, and no future module is withheld from the project so that people have to buy it. That is
  the Umbraco model, and it is the thing the comparison below calls out as their weakness.
- **BaryoDev also builds bespoke modules for clients**, and sells delivery, hosting and support.
  That is consulting output: someone paid for work that solves their problem, and nobody was
  expecting it in the open source project.

The contributor terms make the first half structural rather than a promise: the licence grant is
bounded to OSI-approved licences, so this cannot be closed later even if someone wanted to.

## The thesis

The commercial CMS price list was built on an assumption that is no longer safe: that setting a
system up and keeping it running is expensive human labour, and that a licence is the cheaper way to
buy that labour.

Umbraco's €2,800/yr Deploy licence and €250/domain Forms are priced against the cost of building
those things yourself. Heartcore, from $75/month, is priced against the cost of hosting it yourself,
and its entry tier allows one user, one environment and one language. Directus's $5M revenue cap is a
bet that a company past that size would rather pay than self-support. None of these is a feature
moat. All are bets on the price of setup and operations labour.

If a competent team with coding agents can stand this up, extend it and support it themselves, the
licence is the only line left on the invoice, and ours is zero. **That is the levelling: a two-person
agency and an enterprise get the same system, and neither pays for the privilege of scaling.**

Everything below follows from that. It is why the CLI, the templates, the delivery documentation and
the MCP server outrank feature parity: they are not conveniences, they are the mechanism by which the
free claim becomes actionable rather than theoretical.

**The obligation it creates.** A thesis about scaling has to survive contact with scaling. The first
test of that is closed: the async daemon ran `DaemonMode.Solo`, which Marten documents as assuming
"there is never more than one running system node", so two instances sent every workflow email twice.
Fixed in #238 along with the scheduled sweep, which had the same problem for a different reason.

What remains is #239: during a rolling deploy an old node still duplicates, because the half that does
not participate in the new locking is the half already running. Bounded by deploy duration rather than
permanent, and written down rather than implied.

## Why that claim and not a better-sounding one

Checked against the field in August 2026, this is the only claim that is a matter of published fact
rather than argument.

| Platform | Licence | Where free ends |
| :--- | :--- | :--- |
| **barakoCMS** | MPL-2.0 | Nowhere |
| Umbraco | MIT core | Core and headless are free. Forms €250/domain, Workflow paid, Engage €800/yr, Deploy €2,800/yr, Heartcore hosting from $75/mo |
| Directus | MSCL, not OSI-approved | Self-hosting free only under $5M revenue and 50 staff |
| Strapi | MIT core | All AI; nothing on free, $45/mo minimum |
| Sanity / Storyblok / Contentful | proprietary SaaS | metered AI credits |
| Payload | MIT core | AI is enterprise-tier, sold by demo |

Two rows need care, and one of them cuts against us.

**Umbraco's headless is free.** The Content Delivery API ships in the MIT-licensed CMS: 104 source
files under `src/Umbraco.Cms.Api.Delivery`, verified in their repository rather than their marketing.
What costs money is Heartcore, which is the managed hosting of it, plus the add-ons above. So against
self-hosted Umbraco, "free" is not a differentiator, and their MIT is more permissive than our
MPL-2.0. Saying otherwise is disprovable in thirty seconds and would cost more than the point is
worth.

**Directus is not open source**, though it is widely described that way. Current versions ship under
the Monospace Sustainable Core License, free to self-host only below $5M revenue and 50 employees. An
agency that grows past that has a licensing problem there and none here.

**State the cost of MPL-2.0 first rather than have procurement find it.** It is weak copyleft:
modifications to MPL-licensed files must be shared back, which MIT does not require.

## What is deliberately not the pitch

Three framings were considered and rejected on evidence:

- **"AI-native."** Table stakes by 2026, not differentiation. Sanity, Contentful, Storyblok, Strapi,
  Payload, Directus, WordPress and Umbraco all ship MCP servers; Umbraco's exposes 330+ tools.
  barakoCMS has none. We are behind here, not ahead.
- **"Free self-hosted AI while theirs is metered."** Only half true. The SaaS platforms meter, but
  Directus ships its AI Assistant on the free tier accepting any OpenAI-compatible endpoint including
  Ollama, and Umbraco.AI is MIT with bring-your-own-key across eleven providers. We are at parity.
- **"Umbraco alternative", head-on.** Fifteen years of editor UX is not a gap that closes. The
  opening is the invoice, not the product.

## The support objection, and why it inverts

The standard reason not to choose open source is "who do we call". The answer here is that a coding
agent can do it, because the codebase is documented and extendable and a missing feature can be
built rather than waited for.

That answer is only as true as its two dependencies, so both are treated as product, not chores:

- **Extendable: true and provable today.** Thirteen modules on a versioned `IBarakoModule` contract,
  with five test files covering ordering, schema ownership, configuration scoping and seed isolation.
  A team that needs a feature can add one without forking.
- **Documented: half true today.** Every module ships a tracked README, and `AGENTS.md`,
  `AI_DEVELOPMENT_LIFECYCLE.md`, `CLAUDE.md` and `llms.txt` are all in the repo, which is more
  agent-legible than most of the field. But `.gitignore` excludes `docs/*`, so the design docs a
  contributor actually needs are not in a clone. That is #211, and it is the single thing standing
  between this argument and being true.

The consequence for how this repo is written: comments explaining a non-obvious *why*, decisions
recorded with their reasoning in `DECISIONS.md`, and tests that state the rule rather than the
scenario are not house style for its own sake. They are what makes the support answer hold.

### Who actually provides the support

Not us. **Agencies.** A team that adopts this either supports it themselves or hires someone who
knows it, and the second one is a business other people can run. That is Umbraco's actual moat: HQ
sells Cloud and add-ons, and a partner ecosystem delivers and supports.

The difference in our favour is what an agency has to absorb before it can sell that service. With a
commercial CMS they carry a licence cost into every client conversation. Here the only cost is
learning barakoCMS, and if the documentation is good enough for a coding agent to work from, it is
good enough to onboard an agency developer in days rather than months.

That reframes what "documentation" is for. Not answering one team's question: **letting a third party
build a practice on this without asking us anything.** The test for any doc is whether an agency
developer who has never seen the codebase can deliver a client project from it.

`docs/*`, `docs/roadmap.md` and `ROADMAP.md` were all in `.gitignore`, so a clone got the code and
none of the reasoning. The last two are fixed; `docs/*` is #211.

## Order

Gaps before features. The reason is not hygiene: the first thing the positioning claims is a
Deploy-equivalent that beats a €2,800/yr product, and that module currently has no tests. You cannot
headline a claim you have not tested.

### 3.22.0 — 29 Aug — Nothing ships untested

Six of thirteen shipped packages have no test project reference at all, so they cannot be tested
without a csproj change. Two of them, `ExternalAuth` and `DeviceTrust`, are where a defect is a
breach rather than a bug. `Portability` is the Deploy-equivalent the positioning rests on.

Carries: #200, #222, #223, #217, #211, #155, #147, #130.

### 3.23.0 — 5 Sep — Authorisation is tested, not assumed

Authorisation is covered thoroughly where an incident already happened and absent everywhere else.
Cross-tenant isolation is proven in two halves that never meet.

Carries: #231 (negative-auth on UserGroups, user assignment, workflow tools), #232 (HTTP-level
cross-tenant leak), #233 (two tests that cannot fail).

### 3.24.0 — 12 Sep — The CLI

The missing word in the pitch. `barako new`, `barako up`, scaffold a content type, run migrations,
seed. Supabase's own docs put time-to-running at under thirty minutes and a large part of why teams
choose it is exactly that number.

Also folds in the two cheap event-sourcing one-way doors while they are still cheap: #228 (events
carry `OccurredAt`) and #229 (no event type in any API response).

### 3.25.0 — 19 Sep — Set it up properly

The half of the pitch that says "properly". A team adopting this needs a starting point and a
written path, not an empty database.

Carries: #188 (starter templates), #189 (documented delivery flow for a client project).

### 3.26.0 — 26 Sep — Agents can drive it

MCP server over the content API, plus #185 (report which modules an instance is running) so an agent
can discover what it is looking at rather than guess.

Two shapes worth copying: Strapi generates tools from the content schema, and Directus routes MCP
through its existing permission system rather than inventing an agent-specific one. The MCP spec
itself makes rate limiting and input validation a MUST, and says clients SHOULD log tool use for
audit; for a product claiming audit-grade, that is a floor, not a stretch goal.

### 3.27.0 — 3 Oct — What the app consumes

A typed .NET client, so a MAUI or Blazor consumer does not hand-write one.

Carries: #183 (pick and pin a generator, prove one slice), #186 (the client itself), #187 (notice
when the generated client stops matching the API).

## Standing rules for every release

1. **One announceable sentence per release**, and it names something included that a competitor
   charges for, or a guarantee now proven by a test. If neither is true, the release is a patch.
2. **No module ships without a test that could fail.** See `DECISIONS.md` D7: a test whose expected
   value comes from the code under test is not a test.
3. **Claims in the README are checkable or absent.** The licence table above is the standard: every
   row is a published fact with a source, not a comparison we assert.

## Later, not scheduled

**Feature parity with what Umbraco charges for**: Forms (€250/domain there), approval workflow
(paid add-on there), a media library worth the name. Worth having, and deliberately not first: the
pitch does not need them, and a team choosing on cost is not comparing form builders.

Note the word collision when approval workflow does land: "workflow" here currently means automation
rules. The two do not overlap at all and the docs must say so.

**The backend-as-a-service surface**: realtime subscriptions, schema-derived endpoints, row-level
security. The audit found .NET-native BaaS to be the only genuinely unclaimed category, but each of
these is larger than one Saturday.

The one-way door here — Postgres RLS versus the current C#-side model — is now settled, as
`DECISIONS.md` D11: authorisation stays in the application, the database enforces tenancy only
(#446), and the conditions gain SQL predicates without moving enforcement (#445). That is what makes
the rest of this list buildable rather than blocked. It also sets the condition under which the
decision is wrong, and realtime subscriptions are exactly it: the moment an untrusted client talks
to Postgres directly, the database becomes the only boundary on that path, and D11 has to be
reopened before that feature ships rather than after.
