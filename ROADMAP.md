# Roadmap

Releases ship every Saturday. This file says what each one is for and why that order.

The positioning it serves: **reachable, and free at any scale**. Not "cheaper than", not "has more
features than", and deliberately not "enterprise-ready", which is a claim about being a vendor rather
than about software.

Reachable first, because that is the part nothing else in the field offers: no sales call, no partner
tier, no procurement cycle, no discovery call before you can run it. Clone it and it works.

Free second, and precisely: no seat cap, no revenue cap, no metered AI, and every module BaryoDev
publishes included.

That last phrase is deliberate and worth keeping precise, because three different things get called
"selling modules" and only one of them would break the promise.

- **Every module BaryoDev publishes is free, forever.** Not only the ones in this repository:
  anything we ship under the `barakocms-module` tag, wherever it lives. Nothing gets moved behind a
  licence later, and no future module is held back so that people have to buy it. That is the
  Umbraco model, and it is the thing the comparison below calls out as their weakness.
- **Other vendors may charge for theirs, and core helps them do it.** Licensing is being built into
  core so a third-party module can require a key without every vendor inventing that mechanism
  again, which is the part of the Umbraco marketplace that actually went wrong. What they charge is
  theirs to decide. The promise above is about what BaryoDev publishes, not about what anybody else
  is allowed to sell, and a paid third-party module appearing in the module list is the ecosystem
  working rather than the promise bending.
- **BaryoDev sells delivery, hosting and support**, and builds bespoke modules for clients. That is
  consulting output and operations: someone paid for work that solves their problem, or paid us to
  run it for them. Neither takes anything out of the open source project.

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

### Shipped: 3.22.0 and 3.23.0

3.22.0 closed the gap where six of thirteen shipped packages had no test project reference at all.
3.23.0 covered authorisation where an incident had already happened and nothing else had been
proven.

The weekly train stopped there. What was planned for 3.24.0 through 3.27.0, the CLI, starter
templates, an MCP server and a typed .NET client, was not cancelled; it moved into the numbered
releases below, where it sits against the rest of the work rather than against a date.

### 4.0.0, the contract

Everything in this release is a one-way door: a choice that cannot be made later without breaking
somebody who has already upgraded. That is the whole selection rule, and it is why the milestone
holds defects and contract decisions rather than features.

The door that defined it: content now has optimistic concurrency, an `ETag` on read and `If-Match`
on write. Moving from last-write-wins to a refusal is a breaking change, and it costs nothing while
there are no 4.0 clients. `DECISIONS.md` D16 records why, and why the flag that preserves the 3.x
upgrade path defaults off until 5.0.

Tagging it also publishes `BarakoCMS.Templates` and `BarakoCMS.Testing`, which is what finally lets
somebody outside this repository build a module at all.

### 4.0.1, what the tag makes testable

The upgrade harness needs a published 4.0.0 image to stand up, so the work that proves an upgrade
path can only run once 4.0.0 exists. Hygiene and documentation that missed the tag land here too.

### 4.1.0, the workflow engine grows a spine

An action cannot produce a value today, so a workflow is a list of independent side effects rather
than a chain. Giving actions outputs, a per-action failure policy and a shared condition evaluator
is the change that turns it into something a business process can be built on.

Also here: the delivery cache by tag rather than by a sixty second window, per-key rate limits, and
the module contract's pipeline hook.

### 5.0.0, a system of record rather than a content store

The primitives every transactional system needs and none of which exist: a header with its lines
written in one transaction, a reservation that decrements under a floor, computed fields, numbering
with the gapless cost made explicit, and money that carries its currency.

Then the surfaces those make possible: the CLI, an MCP server, and a portal generated from the
definitions rather than written per project.

### barakoBrew ships alongside

The console lives in `BaryoDev/barakoBrew` and versions on its own scale, because it is a separate
artifact with a separate release. The pairing is fixed:

| barakoCMS | barakoBrew |
| --- | --- |
| 4.0.0 | 1.0.0 |
| 4.1.0 | 1.1.0 |
| 5.0.0 | 2.0.0 |

So barakoBrew 1.0.0 is the console that goes with this release, and its milestone carries what a
first release actually needs: continuous integration, a licence, a publish pipeline, and the
`If-Match` handling that makes the concurrency above reach an editor.


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

The one-way door here, Postgres RLS versus the current C#-side model, is now settled, as
`DECISIONS.md` D11: authorisation stays in the application, the database enforces tenancy only
(#446), and the conditions gain SQL predicates without moving enforcement (#445). That is what makes
the rest of this list buildable rather than blocked. It also sets the condition under which the
decision is wrong, and realtime subscriptions are exactly it: the moment an untrusted client talks
to Postgres directly, the database becomes the only boundary on that path, and D11 has to be
reopened before that feature ships rather than after.
