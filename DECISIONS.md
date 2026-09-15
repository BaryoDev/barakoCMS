# Decisions

Choices that are expensive or impossible to reverse, with the reasoning that produced them.

A decision belongs here when someone six months from now would otherwise look at the code, see no
reason for it, and change it back. The reasoning is the point. A decision without one is just a
current state, and this file is not a description of the current state.

Format: what was decided, what it rules out, why, and what would have to change for it to be wrong.

---

## A recurring test: which door stays open?

Most entries below were settled by the same question. When a choice cannot easily be reversed, take
the option that leaves the other option available.

If you can loosen a rule later but not tighten it, ship it tight. If adding a behaviour later would
break callers but removing it would not, add it now. This resolves more arguments than it has any
right to, and it resolves them without anyone having to predict the future correctly.

---

## D1. The event-sourced flag belongs to the content type NAME

**Decided:** 22 Aug 2026. **Issue:** #230. **Status:** accepted, not yet implemented.

A content type declares once whether its content is event sourced, and that choice is permanent.
The flag lives in its own `ContentTypeSourcingPolicy { Name, EventSourced, DecidedAt }` record,
written once per name and never deleted, rather than as a property of `ContentTypeDefinition`.

**Rules out:** storing the flag on the content type document.

**Why.** Storing it on the document leaves a hole. Delete the type, recreate it with the same name
and the opposite flag, and existing streams and documents belong to a type whose rules have changed.

Refusing to delete a type that still has content does not close it, and this is the part that is
easy to get wrong: deleting content and deleting streams are not the same operation. Someone deletes
every document, deletes the type, recreates it, and the streams are all still there.

Keeping the policy outside the type makes the hole structurally impossible rather than enforced by a
rule that six code paths have to remember. The cost is one small document.

**Wrong if:** content type names stop being the stable identity for content. If types are ever keyed
by id with a mutable display name, this moves to that id.

---

## D2. An event-sourced type may not hold non-Public fields

**Decided:** 22 Aug 2026. **Issue:** #230. **Status:** accepted, not yet implemented.

Enforced at type creation and at field-add, using the `FieldDefinition.Sensitivity` that already
exists.

**Rules out:** documenting the erasure limitation and relying on operators to respect it;
tombstone-and-rebuild; crypto-shredding.

**Why.** An immutable event stream and a legal obligation to erase personal data are in direct
conflict, and a CMS with user-defined schemas will hold personal data whatever the documentation
says.

Documenting the limitation is a legal exposure with a technical fix available. Tombstoning appends a
redaction event while the payload remains in earlier events, so it presents as erasure and is not,
which is worse than doing nothing because it produces false confidence. Crypto-shredding is real
erasure and a large build for a feature that currently has no users.

Refusing the combination means personal data structurally cannot enter a stream it cannot be erased
from, and the operator learns this when creating the type rather than when answering a data-subject
request.

The door test applies: this can be relaxed later without breaking anything, and cannot be tightened
later without breaking every type already created.

**Honest limitation:** a Public field can still contain a name. This reduces the exposure, it does
not remove it, and the documentation must say so rather than implying compliance.

**Wrong if:** crypto-shredding gets built. Then the restriction can be lifted for types that use it.

---

## D3. Event-sourced types use expected-version concurrency

**Decided:** 22 Aug 2026. **Issue:** #230. **Status:** accepted, not yet implemented.

A write to an event-sourced type made against a stale read is rejected with 409. Other types keep
last-write-wins.

**Rules out:** matching last-write-wins everywhere for API consistency.

**Why.** The door test decides it. Moving from last-write-wins to expected-version later is a
breaking change, because clients begin receiving a status they never handled. Moving the other way
breaks nothing: you stop returning 409.

It is also better behaviour on its own terms. Today two editors silently overwrite each other, which
is a defect that has been tolerated rather than a design that was chosen.
`IContentWriter.AppendOptimisticAsync` already exists, so the machinery is built.

**Accepted cost:** two content types in the same API behave differently on a concurrent edit. This
is documented as the direction of travel rather than presented as an inconsistency.

---

## D4. The event stream is internal, and nothing may leak it through the API

**Decided:** 22 Aug 2026. **Issue:** #229. **Status:** accepted, enforced by `EventSurfaceTests`.

History is exposed only as a projected, versioned view. No API response carries an event type name
or an event payload.

**Rules out:** returning raw events from the history endpoint, or adding an event-type discriminator
to `VersionResponse`.

**Why.** Once the stream is the source of truth its shapes have to keep evolving, and upcasters make
that survivable. The moment one response carries an event type, every event shape becomes public API
and reshaping one is a breaking change.

**This constraint currently holds by luck.** `GET /api/contents/{id}/history` already returns a
projected DTO, and it does so because whoever wrote it projected out of ordinary API hygiene, not
because anyone was thinking about event evolution. That is exactly the kind of invariant that gets
removed by a reasonable-sounding request: a client wants to distinguish a status change from an
edit, someone adds `EventType`, and the cost is invisible for a year.

So the decision is not "do not expose it". It is "do not expose it, and make that mechanical", via a
test that fails when a response model references `barakoCMS.Events.*`.

`EventSurfaceTests` is that test. It reads the response types off the endpoints themselves, by
walking each endpoint's base chain to the `Endpoint<TRequest, TResponse>` it collapses to, so a
response added next year is covered without anyone remembering to list it. From each response it
follows property types, constructor parameters, public fields, array elements and generic arguments,
because a `List<ContentCreated>` or a `Dictionary<string, ContentUpdated>` is the same leak one level
down and a positional record carries its payload in a constructor parameter before it is ever a
property.

It carries its own controls: that the query found the real response surface rather than an empty set,
and that the walk does report a leak when one is planted. A reflection guard with a typo finds
nothing and passes, which is a failure this project has shipped before.

---

## D5. Events carry when they happened

**Decided:** 22 Aug 2026. **Issue:** #228. **Status:** accepted, not yet implemented.

Every content event carries `OccurredAt`, set once by `IContentWriter`. `Content.Apply` reads it
from the event rather than taking it as a parameter or reading the clock.

**Rules out:** deriving the document's timestamps from Marten's `IEvent.Timestamp` at rebuild time;
tolerating the drift.

**Why.** Two clocks were answering the same question. `ContentWriter` stamped `DateTime.UtcNow` as
it applied an event; Marten stamped the transaction time at commit. A rebuild can only see the
second, so a rebuilt document's timestamps differ from the original by the write latency.

This was not found by reasoning about it. It was found because the rebuild test in #227 could only
assert the timestamps within a tolerance, and the reason it could not do better was a real defect
rather than a limitation of the test.

The fix separates two things that were being conflated:

| | Meaning | Source |
| :--- | :--- | :--- |
| `event.OccurredAt` | when the change happened | the writer, once |
| Marten `IEvent.Timestamp` | when it was recorded | the database, monotonic |

Domain time drives the projection; storage time drives ordering. That split matters in a
multi-instance deployment, where application clocks can skew and the database clock cannot.

**Secondary effect worth recording:** `Apply(@event, DateTime occurredAt)` was the only reason #227
had to break three public `Apply` signatures and add obsolete overloads. With the time on the event,
`Apply(@event)` keeps its original shape and the break never happens. Shipping a breaking signature
change and reversing it later is worse than not shipping it.

---

## D6. Every write to content goes through one writer

**Decided:** 21 Aug 2026. **Issues:** #222, #223. **PR:** #227. **Status:** implemented.

All content writes go through `IContentWriter`. An event with no matching `Content.Apply` overload
throws rather than appending cleanly.

**Rules out:** endpoints appending events and storing documents themselves.

**Why.** Ten write paths across four assemblies each held their own copy of what recording content
means, and they had drifted. Four appended no event at all, and three fields were carried by no
event, so anything reconstructing state from the stream would have lost them silently. `Sensitivity`
was the serious one: losing it does not produce a broken record, it produces a readable one.

`CLAUDE.md` says not to add a service reflexively. This one is justified on its own terms: the logic
is genuinely shared across ten call sites and complex enough to test alone. The alternative was the
same routing decision written ten times, drifting invisibly, because both branches produce a
valid-looking document.

**Throwing on an unmatched event is deliberate.** Appending an event with no projection would
succeed and leave the document unchanged, which reads as a successful save and only surfaces later
as a document that disagrees with its own history. Failing the write is louder and cheaper.

---

## D7. A rebuild test must not compare against a stored document

**Decided:** 22 Aug 2026. **PR:** #227. **Status:** implemented.

`ContentStreamRebuildTests` asserts the rebuilt document against literal values from its arrange
step, never against the document the writer produced.

**Rules out:** the obvious version of the test.

**Why.** The first version of that test compared the rebuild to the stored document, and **it passed
with `Sensitivity` deleted from `Apply`**. The stored document is produced by the same `Apply`
overloads through the writer, so breaking one loses the field on both sides and they still agree. It
tested that `Apply` is deterministic, which nobody doubted.

Verified by deleting `Sensitivity` from `Apply(ContentSensitivityChanged)` and
`ScheduledUnpublishAt` from `Apply(ContentScheduled)` in turn. Both go red now; neither did before.

The general rule this is an instance of: **when the expected value and the actual value come from
the same code, the test cannot fail.** Assert against something known independently of the code
under test.

---

## D8. Backfills must make a partial run visible

**Decided:** 22 Aug 2026. **Issue:** #167. **Status:** open.

A batched backfill logs per batch, and says so loudly when a run does not reach the end.

**Why.** `DataSeeder.BackfillSearchTextAsync` runs inside an un-awaited `Task.Run` whose catch only
logs. On a large corpus the backfill can fail, the exception becomes one log line, and the
application serves traffic normally with public search returning nothing for pre-existing content,
indefinitely. A crash would be better.

Batching, added in #197, fixed the memory and transaction problem and made this one slightly worse:
each batch now commits independently, so a mid-run failure leaves a partially backfilled corpus
rather than none at all. It resumes on the next boot, but between the crash and that restart the
site quietly serves incomplete results.

**The general rule:** a process that can partially complete must make partial completion visible.
Otherwise a failed run and a successful one look identical afterwards, and nobody checks the one
place they differ.

---

## D9. Erasure is a configured mode, and a mode that cannot deliver is refused at startup

**Decided:** 30 Aug 2026. **Issue:** #301. **Status:** implemented for `Delete` and `None`.

A deployment chooses how erasure works, through `Erasure:Mode`:

- **`Delete`** (the default). `DELETE /api/contents/{id}/erase` removes the item's events, its
  stream and its read-model document in one transaction, together with the audit entry recording
  that it happened. The item's history goes with it, which is what erasure means.
- **`CryptoShred`**. Content event payloads encrypted per subject; erasure destroys the key.
  **Refused at startup in every deployment**, because it is not implemented and the subject question
  below is open.
- **`None`**. Pure append-only, with no erasure path, for a deployment that has decided its content
  never holds personal data. Requires an explicit acknowledgement, not just leaving a setting unset.

**A note on the name.** This mode was called `Compact` when the decision was written, because
Marten's `CompactStreamAsync` looked like the supported mechanism. It is not, and a spike written
before the implementation is what caught it: compaction requires a registered aggregation
projection, which this project has none for `Content` since the read model is written by
`IContentWriter` in the same transaction, and even with one it replaces the events with a snapshot of
current state, which is precisely the data an erasure removes. `ArchiveStream` is softer still: it
sets a flag and leaves every byte. So erasure is a delete below Marten's API, and the mode is named
for what it does rather than for the API that turned out not to do it.

**Rules out:** picking one mechanism for everyone, and letting an operator change mode freely.

**Why a mode rather than a mechanism.** The three options in `EVENT-SOURCING-PER-CONTENT-TYPE.md`
are not really alternatives, they are different prices for different guarantees, and which one a
deployment needs depends on whether it holds personal data at all. A newsroom publishing articles
and an agency holding client contact details want different answers, and neither should pay for the
other's.

**Why `Delete` is the default.** It is the only mode that works on data already written. Every
existing deployment gains a real erasure path on upgrade with no migration and no key management,
and it needs no answer to the subject-mapping question below.

**Why the guard is the entire point.** Two failures share one shape, and both are a setting that
reads as a policy while no policy is in force.

`CryptoShred` is unimplemented, so accepting the setting would give an operator who has decided they
need real erasure the belief without the property. It is therefore refused in every deployment, not
only on one that already holds plaintext events, until the subject question is answered.

And when it is implemented, the retroactivity guard still applies: crypto-shredding cannot be applied
to an event already written in plaintext, so switching in year two protects nothing written in year
one. Either way the answer is to fail at startup rather than let the belief form.

The transitions are deliberately asymmetric. Starting on `CryptoShred` keeps every option, because
shredded data can also be compacted. Starting on `Delete` forecloses shredding for everything
written before the switch. Given the choice, this is the door that stays open.

**What is still unanswered:** who the subject is. Crypto-shredding needs a key per something, and a
CMS has no natural data subject, because a blog post that mentions a person is not owned by them.
Two implementable variants, and `CryptoShred` cannot ship without choosing one:

- a **per-tenant** key, which gives irrecoverable customer offboarding but is not Article 17 for an
  individual;
- a **per-subject** key, which needs a content type to declare which field identifies the subject,
  making it a schema feature rather than a configuration value.

**Also unresolved, and named here so it is not discovered later:** the audit trail is a second
erasure surface. `AuditEvent` carries `ActorUsername` and metadata, and `AuditChain` hashes each
entry over its predecessor, so deleting one breaks the tamper-evidence the chain exists to provide.
Erasure and tamper-evidence are in direct conflict there too, and this decision does not settle it.

**What would have to change for this to be wrong.** If content turns out to hold personal data in
the ordinary case rather than the exceptional one, the default is backwards: `CryptoShred` should be
the default and `Delete` the opt-out. The signal to watch is what customers actually model in their
first content type.


---

## D10. An unverified self-registration is not an account, it is a pending row

**Decided:** 1 Sep 2026. **Issue:** #268. **Status:** implemented.

`POST /api/auth/register` writes a `PendingRegistration`, not a `User`. The user document is created
by `POST /api/auth/register/verify`, when the address named at registration hands back the
single-use token that was emailed to it. Until that happens there is no account and no username
held. The pending row itself does carry the submitted address and username, which is the point: they
are held there, out of the users table, until somebody proves the address or the row is cleaned up,
so there is no user document for an external provider to match onto.

**What this rules out.** The obvious alternative, and the one the issue suggested: keep creating the
user and carry an `EmailVerified` flag, then refuse login (or issue a restricted session) until it
is set. That was rejected.

**Why.** The email address is a join key, not just a contact field. `SocialSignIn.IssueAsync` matches
a provider's verified email to a local account by address alone, which is why the providers were
hardened to require `email_verified` from Google and LinkedIn, to read only the verified primary from
GitHub, and to refuse Facebook unless an operator opts in. That path never looks at a password, a
status or a flag on the way in, so a flag on `User` would not have closed anything: register as
somebody else's address, wait for them to sign in with Google, and the provider puts them into your
account. Only the absence of the row closes it.

Two smaller reasons point the same way. A flag needs a backfill, because Marten deserialises a field
that is not in the stored JSON as its default, so every account that existed before the upgrade would
read as unverified and be locked out by its own security fix. And a pending row that is not a user
cannot hold a username, so an anonymous caller cannot squat names without ever owning a mailbox.

**What it costs.** A username is not reserved between registering and confirming. Two people can hold
pending registrations for the same name; the first to confirm gets it and the second is refused at
verification with the same message every other rejection there uses. That is the right way round: the
reservation is the thing an attacker would want for free.

**Verification is required by default**, which is the one place in the codebase where a new setting
does not preserve existing behaviour. What it would preserve is the defect. Turning it off with
`Auth:RequireEmailVerification=false` is a legitimate choice for a deployment with no mail transport
or a registration form nobody outside can reach, and it needs `Auth:AcknowledgeUnverifiedRegistration`
to start, the same shape D9 uses for `Erasure:Mode=None` and for the same reason: arriving at it by
leaving a key unset is not a decision.

**What would have to change for this to be wrong.** If the address ever stops being the join key, if
external sign-in matched on a provider subject id recorded at first link instead, then a flag on
`User` would be enough and the pending row would be ceremony. That is a better design for the
external providers anyway (an address can change hands), and if it is ever built, this decision is
the one to revisit.

---

## D11. Authorisation is enforced in the application; the database enforces tenancy only

**Decided:** 2 Sep 2026. **Issues:** #445, #446. **Status:** decided; both pieces of work outstanding.

`IPermissionResolver` is the authorisation boundary. Content CRUD, row-level conditions, field
sensitivity and the SuperAdmin bypass are decided in C#, against a database connection that is
already trusted, and that is where they stay. Postgres gets exactly one enforcement job, the
`tenant_id` discriminator (#446), and it gets that as a backstop behind a flag, not as the place the
rules live.

The condition language is frozen as a contract at `_eq`, `_ne`, `_in`, `_nin` and `$CURRENT_USER`.
Every role document in every deployment is written against it, and #445 makes it a second
implementation, so adding an operator means adding it in two places at once or not at all.

**Rules out:** the Supabase shape. No PostgREST-style layer that maps HTTP straight onto SQL, no
per-end-user Postgres role, no row-level security carrying business rules, and no browser holding a
database connection.

**Why not put the rules in the database.** The attraction is real: one enforcement point, no way to
forget a check in a new endpoint. It does not survive contact with what is actually stored here.

Field sensitivity is the fact that settles it. `FieldDefinition.Sensitivity`, `VisibleToRoles` and
`Mask` do not decide whether a row is returned, they decide what a returned row *contains*: `SSN`
removed for one caller, `BirthDay` masked to `***` for another, both present for a third, all from
one JSONB document. Row-level security filters rows. It has nothing to say about the inside of one,
so the most sensitive control in `docs/access-control.md` would have to stay in C# no matter what,
and a boundary that holds two of three layers is not a boundary, it is a second copy of the rules
with a gap in it.

The other half is that there is nothing on the other side of the boundary to protect against. Supabase
puts RLS between an untrusted browser and the database because the browser genuinely holds a
connection. Here every statement is issued by our own process, after FastEndpoints has run the
permission check, over a connection string the operator controls. Policies against that connection do
not defend against an attacker; they defend against our own bug, which is worth having for one flat,
mechanical predicate like `tenant_id`, not worth having as a duplicate of the whole permission model.

And the costs are not hypothetical. Per-request `SET ROLE` needs `SessionOptions.ForConnection`,
which puts Marten into sticky mode: the connection footprint stops tracking active statements and
starts tracking concurrent requests. Session-level `SET` breaks silently behind a transaction-pooling
PgBouncer. A table's owner bypasses RLS unless the table is `FORCE ROW LEVEL SECURITY`, so the
obvious single-user deployment enables policies that never fire. Each of those is payable for tenancy.
None is payable for a rule that C# already enforces correctly.

**What is worth taking from the other design, and is taken.** Two things, both additive:

- **Predicates, not enforcement (#445).** Compiling conditions to a jsonb `WHERE` fragment is the
  valuable half of "policies as data" and needs none of the boundary move. Today `Features/Content/List`
  loads the whole collection and filters per item; a predicate makes the rules usable as a query
  filter, so the cost tracks the page rather than the table.
- **Tenancy at the database (#446).** `tenant_id` is a column Marten already manages. A policy on it
  bounds every request-path session opened without a tenant. It would **not** have caught #287. The
  workflow daemon runs as table owner and legitimately crosses tenants, and that fix was its own. And
  the issue says so, because a backstop sold as catching the bug that motivated it is how a control
  gets trusted for something it does not do.

**What would have to change for this to be wrong.** If BarakoCMS ever grows a path where an untrusted
client talks to Postgres directly (a realtime subscription, a published read replica, an embedded
SQL surface for customers) then the database is the only boundary that exists on that path and the
answer flips. The signal to watch is a feature request for direct data access that does not go through
the API.

## D12. Scheduled is a real content status, not a condition derived from a date

**Decided:** 2 Sep 2026. **Issue:** #440. **Status:** implemented.

`ContentStatus` gains a fourth member, `Scheduled = 3`. Arming a publish time on a draft moves the
entry to it, clearing that time moves it back, and the sweeper promotes it to `Published` when the
time arrives. A published entry carrying a future unpublish time stays `Published`, because it is
published: the pending change does not un-publish anything in the meantime.

**What this rules out.** Leaving it derived, which is what the admin does today: a draft with a
non-null `ScheduledPublishAt` is shown as scheduled by whoever is looking at it. That keeps the write
path and the sweeper untouched and needs no migration, and it was the cheaper answer.

It was not taken because the definition does not stay in one place. Every screen, endpoint and report
that wants the distinction has to write the condition out again, and there is nothing to stop two of
them writing it differently. `ScheduledPublishAt` has no index either, so a derived filter scans,
whereas `Status` is indexed twice already, on its own and with `ContentType`. And the lifecycle
itself was untrue: an entry that will publish on Friday is not a draft, and calling it one made the
status column say the wrong thing to everybody reading it.

**What it costs, stated plainly.** The enum is stored as an integer, because Marten's serializer has
no string enum converter (the one in `ServiceCollectionExtensions` is the HTTP serializer). So the
member is appended, never inserted, and `migrations/4.0.0/3.x-to-4.0.sql` backfills existing drafts
that carry a publish time. The rollback puts them back to `Draft`.

Arming a schedule now appends a `ContentStatusChanged` next to the `ContentScheduled`. That is the
rule this project keeps rather than an extra event for its own sake: a status that moved without one
behind it is invisible to `GET /api/contents/{id}/history` and to every workflow watching for a
transition, and a replay would have to invent it. Deriving the status inside
`Content.Apply(ContentScheduled)` would have been three lines and would have broken exactly that.

The one gap is pre-4.0 data. Rows the migration moves have no `ContentStatusChanged` behind them, so
replaying one of those streams gives `Draft` with the date still on it. The sweeper accepts both, so
those entries still publish on time; they would just show under Draft until something writes to them
again.

## D13. The client is a hand-written base plus generated slices, and we do not write the generator

**Decided** 18 August 2026. Cited by #182, #183, #186, #187 and #188.

**The decision.** A small hand-written base client, extended by generated slices, one slice per
OpenAPI tag. Not a hand-written client, and not a wholly generated one.

The base holds what a generator produces badly and what does not change: transport, base URL and
headers, authentication (login, refresh, logout, the token store, `recover()`), the tenant header,
one error shape so a failure is the same object whatever produced it, and the handful of ergonomic
helpers already earning their place (`bySlug`, `menu`, `fileUrl`). None of that is well described by
an OpenAPI document, all of it is what makes the current client pleasant to use, and baryo.dev
depends on it in production.

Everything else is generated: the typed method surface, one slice per tag, which after the tagging
work means one per feature area and one per module.

```ts
const client = createClient({ baseUrl, tenant })   // base: transport, auth, tenancy
client.use(coreApi)          // generated, ships in the package
client.use(accountingApi)    // generated, ships with the Accounting module
client.use(myCrmApi)         // generated from your own instance
```

This is the same shape as the CMS: a small core, and modules you add. Anyone who understands one
understands the other, which is the strongest argument for it.

**What it buys.** A third-party module can never appear in a central document, and now it does not
have to: it publishes a slice, or you generate one from your own instance. A hand-written client
drifts from the API, and now the drifting part regenerates while the part that stays is small enough
not to. And calling a module the instance does not run becomes a compile error rather than a runtime
404, for anyone using types, with no runtime check at all. That last one is why `GET /api/modules`
(#185) is a diagnostic for the convenience case rather than the mechanism.

**Do not write the generator.** Maintaining code-generation templates for TypeScript and C# is a
project in itself, and the kind that quietly becomes the main thing. What ships is a configured
invocation of an existing one: Microsoft Kiota emits both languages from one document, with NSwag and
openapi-generator as alternatives. The deliverable is a command, a pinned generator version and a
config file. Which reframes the effort: the tag convention is the actual work, because tags become
slices, and everything downstream is configuration.

**What this rules out.** Writing the client by hand, which is where this started. It reads well and
it drifts, and every module author has to be talked into contributing to it. Also ruled out:
generating the whole thing, which produces one `ApiClient` class with every method on it and none of
the ergonomics, and is the reason "generated clients are unpleasant" is a fair objection to the
naive version of this.

**Status of the thing it depended on.** The design recorded that 76 of 79 operations carried the
single tag `Api`, so generating would have produced one flat class. That was a defect in the source
document rather than a limit of generation, and fixing it at the source fixed it for every language
at once. It is fixed: `OpenApiTagTests` now pins the tag set, and a new feature area that does not
tag itself fails that test.

**Still open, deliberately.** Which generator, proven end to end on one slice (#183), and whether a
.NET client ships at all (#186). Neither is decided here.

---

## D14. The delivery API is not versioned in the URL; it follows the package version

**Decided** 4 September 2026. Cited by #107.

**The decision.** Every route under `/api/public` follows the semantic version of the package that
registers it: the core for the routes in `docs/delivery-api.md`, the module for a route a module adds
under the prefix (`BarakoCMS.Files`, `BarakoCMS.AI`). Modules version independently of the core, so a
module route breaks only in that module's major. A breaking change to a route, a response shape, a
filter's meaning or a default lands only in a major, is announced in `CHANGELOG.md` under a Delivery
API lead at least one minor before that major, and is marked deprecated in `docs/delivery-api.md` on
the same schedule. Additive changes land in a minor. A security fix, whatever it closes, ships in
the next release whatever its number. The policy text lives in `docs/delivery-api.md` under
"Stability and deprecation".

**What it rules out.** A version segment in the URL (`/api/v1/public`), a version header, or a
version query parameter. FastEndpoints supports all three and the issue named the URL form as the
most legible and the easiest to cache, which is true.

**Why.** A versioned delivery API is two code paths, two projection rule sets and two test suites
kept in step, for as long as the old version is promised to work. That is a standing cost paid on
every change to the surface, and this project is not large enough to spend it. The alternative is
cheap: a written rule about when a break may ship and how it is announced, and a changelog that
already carries per-release sections.

The change that raised the question, 3.20.0 making public delivery opt-in, also does not support
versioning. It was a break shipped in a minor with no notice. A `v2` would have carried the same
break to anyone who moved to it, and a `v1` kept alive would have kept serving the data exposure it
closed. That was a policy failure, and the fix for a policy failure is a policy.

**What would have to change for this to be wrong.** A second consumer class with a long upgrade
cycle, such as a native app store review queue, that cannot take a breaking change on the notice
window and cannot pin the package version it talks to. If that appears, the door is still open:
adding a `v2` prefix later breaks nothing, while removing one that exists would.

## D15. The queue owns retry, and an enqueue rides the request's session

**Decided:** 4 Sep 2026. **Issue:** #106. **Status:** implemented for the queue itself; the
consumers move in follow-ups.

**The queue owns retry.** A job record carries the attempt count, the next attempt time, the last
error and a dead-letter state from the first version, and webhook delivery (#95), email and AI
indexing become jobs on it. The alternative was each consumer retrying on its own: #95 has a
delivery log with an attempt field and could have grown a loop around it. That is three retry
policies to explain, three backoff tables, and three places a failed send can be found, and the
argument in the issue thread was that one mechanism serving both is the better reason to build the
queue at all. So the record has the fields now, because a record without them cannot be upgraded
into one that has them without a migration.

**The transactional answer.** `IJobStorageProvider.StoreJobAsync` receives the record and a token
and nothing else, and the provider is a singleton, so the caller's session cannot arrive by
injection. It is reachable anyway: `IHttpContextAccessor` gives the current request, and its service
scope holds the one scoped `IDocumentSession` the endpoint is writing with. The provider stages the
job there, and it commits when the endpoint calls `SaveChangesAsync`, or not at all.
`TransactionalEnqueueTests` proves it in both directions, including the harder one from the issue:
the store succeeded, the commit that followed failed on a unique index, and no job was left.

That is a property of the request, not of the contract, and it is written down as two rules on the
endpoint: queue from a request that writes through the scoped session, and save afterwards. The
outbox shape (a job document written through the caller's session and a poller handing due rows to
the queue) was the fallback if the session could not be reached, and it was not needed. What the
provider does is close to it anyway, since the queue's own worker polls the same table; the
difference is that FastEndpoints owns the polling and the claim, and nothing was written twice.

**What it costs.** FastEndpoints wakes the worker as soon as `StoreJobAsync` returns, which is
before the commit, so that wake finds nothing. A Marten session listener fires a second wake after
the commit. Outside a request there is no scope to share, so a job queued from a background service
commits on its own in the default tenant. And a request that queues and never saves discards the
job; the provider logs a warning naming the request when that happens on a successful response, and
the docs say so.


---

## D16. Document types get expected-version concurrency too, and 4.0 is the only free moment

**Decided:** 5 Sept 2026. **Issue:** #565. **Status:** implemented.

`Content` gets Marten optimistic concurrency. `GET` returns the document version as an `ETag` and
`PUT` accepts `If-Match`, answering 412 when it does not match. Whether a write that carries no
version at all is refused is controlled by `Content:Concurrency:Require`, which defaults to false in
4.x and to true in 5.0.

**Rules out:** leaving document types on last-write-wins indefinitely, which is what D3 accepted as
an interim state; adding this after the 4.0 tag.

**Why.** D3 already made the argument and then scoped it to event-sourced types, calling the
difference the direction of travel rather than a design. This finishes it, and the timing is the
whole point.

Moving from last-write-wins to expected-version is a breaking change: a client starts receiving a
status it never handled. That cost is zero today, because 4.0 has not been tagged and there are no
4.0 clients to break. It is not zero the day after the tag. So the choice is between doing it for
free now and paying for it in a major later, or never doing it and leaving `Content`, the thing the
product is actually about, as the one document that loses writes silently while `JobRecord`,
`OtpCode`, `RefreshToken` and `MfaSecret` are all protected.

The defect is real and not theoretical. Two editors open an entry, the second saves, the first
saves, and the second edit is gone with no error. The history in `Features/Content/History` then
records the first editor's write as the change, so the trail says the second edit never happened
rather than that it was overwritten.

**Why the flag, and why it is not a contract flag.** A 3.x client that upgrades does read-modify-write
without any version, and refusing every one of those writes is not a migration path. The flag is the
same shape as `Lifecycle:EnforceTransitions`, which exists for exactly this reason: a deployment
adopting a new rule has data and callers that predate it.

The flag decides reachability, not membership. `ETag` and `If-Match` ship in 4.0 unconditionally and
are part of the contract from the tag onward, so a client can opt into safety on day one. The flag
only decides what happens to a caller that says nothing, and its default moves in 5.0 with the
change announced in the 4.x notes.

**Accepted cost:** for the whole of 4.x, a client that sends no version still gets last-write-wins.
The defect is fixable by the caller rather than fixed for them. That is the price of an upgrade path
that works, and it is bounded because the default flips in the next major.

**Wrong if:** the 3.x upgrade path stops mattering, in which case the flag defaults to true
immediately and there is no interim.

---

## D17. A money value stays a number; currency and rounding live on the field definition

**Decided:** 5 Sept 2026. **Issue:** #581. **Status:** accepted, not yet implemented.

The `money` field type keeps storing a plain JSON number in `Content.Data`. Currency, scale,
rounding rule and any non-negative constraint are declared on `FieldDefinition` and enforced on
write.

**Rules out:** storing money as `{ "amount": 1200, "currency": "PHP" }`.

**Why.** The stored value shape is the door, and the feature is not. Today `money` is validated by
the same predicate as `decimal`, so a money value is a number, and the delivery OpenAPI document
describes it as `number`. Changing the stored shape rewrites every existing value and breaks every
generated client and every consumer that reads the field, in a system whose entire delivery
promise is that a client can rely on the described shape.

Declaring the metadata on the definition costs nothing to reverse and changes no stored data. It
also puts the rules where the other field rules already live, and where `FieldTypeRegistry` is
already the single source of what a type accepts.

This is recorded before the feature is built precisely because it is the kind of choice that gets
made by whoever writes the first line of code, on the grounds that an object is tidier.

**The case it does not cover.** One field holding amounts in different currencies per entry. That
needs the currency stored per entry, and the answer is a sibling field the definition points at,
not an object in the value. Multi-currency conversion, with a rate source and a rate date, is a
separate feature and conflating the two makes both worse.

**Wrong if:** a single field genuinely has to carry its own currency and a sibling field cannot
express it. No requirement seen so far needs that.

---

## D18. What module authors are promised, and what they are not

**Decided:** 5 Sept 2026. **Issues:** #557, #575. **Status:** accepted, not yet implemented.

Three commitments to anybody building a module outside this repository.

**One. `IModuleSchema` gains projections and event registration before `ConfigureMarten` is
removed.** `ConfigureMarten` is `[Obsolete]` and scheduled for removal in 5.0, and it is currently
the only way a module can register a projection or an event type, because `IModuleSchema` exposes
only `For<T>()`. Removing it in 5.0 without a replacement would delete the only route without ever
having offered another.

**Two. A member added to `IBarakoModule` or `IWorkflowAction` arrives with a default implementation,
and the member it replaces is marked `[Obsolete]` with the removal major named in the message.** That
is what `ExecuteAsync` and `RunAsync` already did, and it is now the stated rule rather than a
precedent somebody might not notice. `ModuleContract.Version` moves only when a member is removed or
a hook's call order changes, which is what `ModuleContract` already says.

**Three. `IWorkflowAction` is a supported extension point and is documented as one.** It is public
because custom actions are an extension point, and `MODULES.md` has never mentioned it, so an author
writing one reads core's source. A contract nobody documents is a contract nobody can rely on.

**Rules out:** inviting outside module authors while the only route to a projection is a hook we have
announced we are deleting; changing an extension point without a deprecation window; treating
`MODULES.md` as covering the module contract when it covers only part of it.

**Why.** The ecosystem cannot start before the 4.0 tag, because `BarakoCMS.Templates` and
`BarakoCMS.Testing` are not published and the template pins 3.21.0. That makes this the moment to
decide what is promised, while the number of outside modules is zero and nothing has to be
migrated.

It also fixes an asymmetry that is easy to miss. 4.0 made 188 `Features` types internal and moved
the target framework, and `ModuleContract.Version` stayed at 1 throughout, correctly, because the
contract itself did not change. A module compiled against 3.21 therefore gets no startup refusal and
no version signal, and fails at the first call instead. Startup should log the core version each
module assembly was compiled against, and `GET /api/modules` should report the supported range.

**Wrong if:** modules stop being a supported extension point and become an internal implementation
detail, in which case none of this is owed to anybody.


---

## D19. Durable execution stays ours, and a workflow definition stays data

**Decided:** 9 Sept 2026. **Status:** accepted.

barakoCMS does not adopt Temporal, in core or as a module BaryoDev publishes. The workflow runner and
the job queue stay the durable execution substrate, and `WorkflowDefinition` stays a document rather
than becoming code.

Temporal was evaluated properly rather than dismissed. It would have supplied durable timers,
suspend and resume, values flowing between steps, and determinism replay. Priced against what is
already here, that list does not survive.

**We already have the substrate.** `WorkflowRun` carries a lease, optimistic concurrency, exponential
backoff with jitter, a stable idempotency key, an `Unknown` state for a timeout whose outcome nobody
knows, and `TriggeringEventSequence` so a retry can tell it has been overtaken. `JobRecord` carries
attempts, a next-attempt time, a dead-letter state and a durable `ExecuteAfter`. Long timers are not a
gap: `ExecuteAfter` in the future is one, and `ScheduledContentService` already sweeps every tenant
every minute. The 600 second cap in `WorkflowRetryPolicy.Backoff` bounds a retry, not a wait.

**A definition is data, and Temporal's model is code.** A Temporal workflow is a C# method, which is
what makes its output binding and its versioning work. `WorkflowDefinition` is a document an operator
edits in barakoBrew: a list of `WorkflowAction`, each a type and a string dictionary. Running that on
Temporal means writing an interpreter workflow that walks the definition and dispatches activities, so
the DSL, the output binding and the condition evaluator are ours to build either way. Temporal would
have supplied only the layer we already have.

Determinism replay follows from the same difference. It exists because code can change under a running
instance. A definition cannot, because `WorkflowRunQueue` copies the parameters into the run when it is
queued. Adopting Temporal would have imported the problem and then solved it.

**It breaks D15.** An enqueue rides the caller's `IDocumentSession` and commits with it, proven both
directions by `TransactionalEnqueueTests`. Starting a Temporal workflow is an RPC to another process
and cannot join that transaction. Keeping the property means an outbox in front of Temporal, which
means keeping `JobRecord`. Temporal does not let us delete anything.

**The footprint contradicts the claim.** The deployment story is an app and Postgres. Self-hosted
Temporal adds a server, two more databases with their own schema tooling and their own vacuum tuning,
and a UI. The positioning in `ROADMAP.md` is that there is nothing to procure and nothing to stand up,
and Temporal Cloud is metered, which is the shape of thing the licence table there uses to
differentiate. Licensing is not the obstacle; MIT sits fine under MPL-2.0. The footprint is.

**What is given up, honestly.** Temporal's dispatch would structurally fix the rolling-deploy
duplication in #239, where an old node does not participate in the new locking because it is already
running. That is a real loss. It is bounded by deploy duration, it is written down, and it has cheaper
fixes than a server cluster.

**The seam stays open, and core builds it.** This is a decision about what BaryoDev ships, not about
what anyone may build. `IWorkflowAction` already covers extension by adding steps, and a third party
who wants Temporal today writes an action that starts a Temporal workflow and hands off. What that
does not cover is Temporal driving a whole run, because `WorkflowRunner` is internal with no
replacement.

Core adds that seam rather than leaving it closed, and it is routing rather than replacement. A
`WorkflowDefinition` names which executor runs it, defaulting to the built-in one, so a Temporal module
claims only the workflows an operator points at it and every other workflow is untouched. Wholesale
replacement of the runner is deliberately not offered: it would make one module's choice global, which
is not what a module is, and it would put the failure mode of every workflow in a package core does
not ship.

**The interface is the smaller half of that contract.** A replacement executor must keep the
`WorkflowRun` document current, because that document is what `/api/workflow-runs` serves and what
barakoBrew reads, and section 6 now treats that JSON as contract. So what an executor is really
promising is the invariants: attempts advance in order, an outcome is written only by the holder of the
lease, a timeout records `Unknown` rather than a failure, and the idempotency key stays stable across
retries of one action. Those are stated with the interface, not left to be inferred from the built-in
implementation.

**It stays internal until a second implementation proves it.** A seam designed against one caller is a
guess, and section 6 freezes a public member for the rest of the major. There are no modules outside
this repository yet, which is D18's argument for settling module questions now and is equally the
argument for not publishing this one early. It ships internal with the routing in place, and it becomes
public in 5.0 once something other than `WorkflowRunner` has been built against it.

**The boundary that keeps this true.** A workflow definition is a bounded, acyclic list of actions with
conditions. If a process needs a loop, recursion, or a nested sub-workflow, it is code, and it belongs
in an `IWorkflowAction` that a developer writes and tests.

That line is the whole risk of this decision. Building durable execution ourselves is not where this
goes wrong; that part is built and the reasoning is recorded next to each piece of it. It goes wrong if
the definition schema grows conditionals, then loops, then an expression language, until it is an
interpreter nobody can test and every author reads the source to understand. 4.1.0 gives actions
outputs and a shared condition evaluator, and that is the release where the pressure starts.

**Rules out:** a first-party Temporal module; a second durable execution engine shipped in core
alongside the runner and the queue; wholesale replacement of the runner by a module, as opposed to
per-definition routing; a workflow definition that can express iteration or recursion; treating
Temporal Cloud as a supported deployment target for anything BaryoDev ships.

**Why.** Of the four things Temporal offered, one is already built, one is a feature slice on top of
what exists, one Temporal does not actually solve for a data-defined workflow, and one is a problem
only its own programming model creates. What is left is operator tooling we have a better-targeted
version of, and scale we do not have. Against that, a second server, two more databases and a broken
transactional enqueue is not a trade.

**Wrong if:** workflow definitions stop being documents and become code that developers write, compile
and deploy. Then output binding, versioning and determinism replay all become real problems rather than
avoided ones, and the calculus inverts completely. Also wrong if a single deployment ever needs
concurrent runs at a volume where a Postgres-polling runner cannot keep up, which is a different
argument from any made here and should be made with numbers.

## D20. Where a developer extends, and where they do not

**Decided:** 11 Sept 2026. **Status:** accepted.

Four products, and each is extended in exactly one way. barakoCMS by modules. barakoPress by
widgets. BaryoVM by release manifests. barakoBrew by nothing.

**barakoCMS owns the model and the rules.** Content types, permissions, workflows, connectors and
the API. A module adds server behaviour the shape cannot express: a lifecycle hook for an
invariant, an endpoint, a document of its own. The rules live here because this is the only place
they can be enforced. A rule in a renderer is a suggestion.

**barakoBrew presents that model and owns none of it.** It is a client of the API exactly as the
renderer is, and calling it the backend is the mistake this record exists to prevent: logic put
there is logic the API cannot enforce and the renderer cannot reach.

**barakoBrew has no plugin model, and that is deliberate.** One console serves every deployment, so
it cannot load a third party's code without becoming a different console per site. It adapts by
reading data instead: content type definitions, field types, and the block schemas a site
publishes. First-party screens for first-party modules are built into brew and detected by
presence, so one release runs against an API with the module and without it. A third-party module
gets generic CRUD over its content types, which is usually enough, and where it is not the answer
is the next paragraph.

**barakoPress owns the public surface and is extended by widgets.** A widget is a component in the
renderer's registry, optionally backed by a module for its server side. This is where
site-specific interface belongs, including the kind that looks like an application: an operations
dashboard for one client's event is a widget on a page gated by role, not a screen in the console
every other client also sees. The component is hand written; the routing, the session, the
permissions, the deployment and the data access are not.

**The ladder.** Most work never reaches the bottom rung, and saying so is more honest than a
percentage.

1. No code. A blueprint and a renderer config.
2. No code. A workflow and a connector, which is how an integration is described rather than
   written.
3. A widget, when a page needs interface nobody else needs.
4. A module, when the server needs a rule, an endpoint or storage nobody else needs.

A module is the last resort rather than the first move. If a job reaches rung four for something
every client would want, that is a signal the capability belongs in a product rather than in a
project.

**What this rules out.** Brew plugins. Business rules in the renderer. A console screen that only
one deployment can use. Modules written for something a workflow already does.

**Superseded in part by D22** (14 Sept 2026): a widget for one site now ships as a plugin package
enabled per tenant, and the renderer config in rung one becomes site settings read at request time.

## D21. It runs wherever containers run, and BaryoVM is one option rather than the path

**Decided:** 11 Sept 2026. **Status:** accepted.

barakoCMS is a container and a Postgres database. That is the whole hosting requirement, and every
claim about where this runs follows from it: a VM, Azure App Service with Database for PostgreSQL,
AWS Fargate or App Runner with RDS, Cloud Run with Cloud SQL, or Kubernetes with the manifests in
`k8s/`. Images are published multi-arch and pull anonymously.

**Ownership and management are separate decisions.** The industry sells them bundled, so people
assume that owning your software means running your own servers. It does not. A deployment can be
entirely managed, patched by a cloud provider, with point-in-time restore, and still not meter
anybody per seat, per record, per environment or per space. What this project refuses is the
metering, not the convenience.

**Scaling out is already safe, and that is worth saying out loud.** `SchemaApplyLock` takes a
blocking Postgres advisory lock, so several instances starting at once serialise rather than race.
Projections take a per-projection advisory lock, so exactly one process runs each. `/health` answers
a platform probe. Anyone with operational experience asks about concurrent startup first, and the
answer has been good for a while without being written down.

**BaryoVM is one deployment option, not a requirement.** It deploys over SSH to a machine you own,
which makes it the cheapest path and the wrong tool for App Service. On a managed platform the
cloud's own pipeline ships the container, and that is normal. Implying otherwise would make the
whole stack look like it runs one way, which is the opposite of what is true. An agency on Azure
uses barakoCMS, barakoBrew and barakoPress, and Azure does the shipping.

**Multi-tenancy is what makes the economics compound.** One deployment serves many tenants, so an
agency's tenth client costs close to nothing. A hosted platform charges for the tenth the same way
it charged for the first. That is the argument, rather than the monthly total.

**What this commits us to.** Documenting the managed path, not only the VM one (#727). Durable file
storage on every target we claim, which today means Azure has a gap because Blob Storage is not S3
compatible (#728). And not claiming a platform works until somebody has run it there.

**What it rules out.** Positioning this as a self-hosting product. That is a smaller market and a
weaker argument, and it is not even accurate. The position is that you own the software and choose
how much of the operating you want to do.

**Amended by D23** (14 Sept 2026): BaryoVM becomes the one deploy engine behind a click-to-deploy
app, VMs first and then Azure and AWS container platforms. Everything else here stands.

## D22. A site is configuration; developers extend with plugins; rules go to workflows before modules

**Decided:** 14 Sept 2026. **Status:** accepted. **Issue:** #795.

Every site, barakocms.com included, runs the same published barakoCMS, barakoBrew and barakoPress
images. Everything that makes a site that site is data set in barakoBrew: a blueprint, a theme and
site settings (#793), navigation, pages made of blocks, and connectors to outside data. The images
are extended, never customised, and there is no per-site repository.

**The ladder, restated from D20.**

1. **Configuration.** A blueprint, a theme, site settings and pages, all set in barakoBrew.
2. **Workflows, workflow actions and connectors.** Integrations and server-side rules that react to
   an event: lifecycle states, named transitions with a permission per role, a workflow on a
   transition, a connector and request for an outside system. `docs/approval-by-configuration.md`
   builds an approval flow this way with no code.
3. **A plugin.** When no block covers a need, a developer writes one as a plugin package. It reaches
   a deployment through a derived barakoPress image and is enabled per tenant. A block every client
   would want moves into barakoPress.
4. **A module**, only for a rule that must hold inside the write itself, such as a gapless sequence
   or a count that must never go below zero under concurrent saves. A workflow runs after the save
   commits, so it cannot refuse one.

**Identity is read at request time.** barakoPress stops baking site identity and theme into the
build. The index, feed, sitemap and robots read the tenant's settings under its cache tag and are
purged by the publish webhook. This reverses the "identity is build time" choice barakoPress made.

**One renderer serves many domains**, the request host mapped to a tenant (#792).

**The deployment is the boundary.** One set of barakoCMS, barakoBrew and barakoPress on one database
serves sites that belong together and may share plugins. A different system, or sites that must not
share plugins or modules, get their own whole set. Never a second renderer against a shared API.

**Same look, generic behaviour.** Colour, type, spacing and layout match a design exactly. Interactive
pieces are generic configurable blocks that behave the same, not copies of one site's code.

**What this rules out.** Per-site forks of barakoPress. `press.config.ts` literals for identity. Site
specific code in a client repository. A module for something a workflow can do.

**What would make it wrong.** A site that cannot be expressed as blocks and settings without
bending the block model out of shape. rckoronadal.org, baryo.dev and barakocms.com are the three
tests (#722); if one of them needs site code, the ladder needs another rung, not an exception.

## D23. BaryoVM is the one deploy engine: VMs first, then Azure and AWS container platforms

**Decided:** 14 Sept 2026. **Status:** accepted. **Issue:** #800. **Amends:** D21.

BaryoVM becomes the engine behind a click-to-deploy app: the owner connects a VM, an Azure account
or an AWS account and deploys by clicking. A local web UI is the first front end;
a desktop app and an editor extension wrap it later.

- **Phase 1, VMs.** Unchanged in practice from D21: SSH to machines the owner has, with a local or
  managed Postgres.
- **Phase 2, Azure and AWS.** The same images on Azure App Service or Container Apps and AWS App
  Runner or ECS on Fargate, through the Azure CLI and AWS CLI the owner is signed into. BaryoVM
  stores no cloud credential.

**Two ways to publish, one manifest.** Directly, building any derived image (an added module, a
barakoPress plugin) on the owner's machine or a build host and pushing it to a registry; or through
CI/CD, with BaryoVM writing the workflow into the owner's repository. A custom image is never built
on a small client VM.

**What stays true from D21.** barakoCMS still runs wherever a container and Postgres run, and a team
shipping with its own cloud pipeline is still supported. BaryoVM is the path offered, not a
requirement.

**What would make it wrong.** A cloud target whose deploy cannot be driven from its own CLI without
BaryoVM holding a long-lived credential. That target stays the cloud pipeline's job.

## D24. The engines stay MPL-2.0; everything built on them is MIT

**Decided:** 14 Sept 2026. **Status:** accepted. **Issue:** #815.

"The core, the soul, the bitterness of the coffee stays MPL-2.0. The rest is on the house." The
bitterness is the three engines.

- **MPL-2.0:** barakoCMS core (`barakoCMS/`), BaryoVM, and the barako CLI.
- **MIT:** everything built on them: barakoCMS modules, `BarakoCMS.Testing`, `BarakoCMS.Suite` and
  the module template, barakoBrew, barakoPress, create-barako-app, and the messaging and worker
  services.

Each product changes from its next release. Versions already published keep the licence they
shipped with.

**Why.** MPL-2.0 was chosen so improvements come back, and that still matters for the engines
everything depends on: the API every product calls, and the deploy tool and CLI that act on live
systems. It applies per file, so it never reaches MIT code that uses an engine. For everything built
on the engines, copyleft only adds a procurement conversation, and MIT removes it for modules,
plugins, consoles, sites and anything sold on top of them.

**The door stays open.** An engine can move to MIT later with a changelog line. A release made under
MIT can never be pulled back under copyleft, so the engines keep copyleft until there is a reason to
drop it.

**Consent.** Modules and barakoBrew move under the existing contributor terms, which name MIT, and
contributions before 23 August 2026 were made under terms that granted relicensing. BaryoVM stays
MPL-2.0, so no outside agreement is needed there.

**The boundary.** Code copied from an engine into an MIT package keeps its MPL-2.0 notice. Moving
files across the line is deliberate and reviewed.

**What would make it wrong.** A module or client needing an engine file under MIT to be usable at
all. MPL-2.0 permits use in closed products, so that should not happen; if it does, it is a reason
to revisit D24, not to copy the file.

## D25. A tenant is a data boundary, not a site

**Decided:** 15 Sept 2026. **Status:** accepted. **Issue:** #935.

A tenant is an entity whose data is visible only to itself: a hotel branch, a school, a client. Tenants
can run the same processes, and their data can be aggregated above them. A tenant is not a site. One
tenant per site is a common setup, not a rule.

- **Isolation.** A tenant's entries, files, events and runs are readable only inside that tenant.
  Database enforcement (`Tenancy:DatabaseEnforcement`) is the backstop.
- **Similar processes.** Content types, workflows, lifecycles and roles can be defined once for a group
  of tenants and used by each of them (#945). A tenant may add its own beside them.
- **Aggregation.** A principal holding a group capability can read totals and lists across the tenants
  of the group, run per tenant so isolation still holds (#946). A tenant member never sees another
  tenant's rows.
- **Groups.** The group is the account above tenants (#898).
- **Sites.** A site is configuration a tenant holds (D22). A tenant may hold one site, several, or none
  when it only serves an API.

**Why.** A hotel chain, a franchise, a school district and an agency all need the same three things:
each unit sees only its own data, the units share one way of working, and someone above them sees the
whole. Treating tenant as site would force a chain to choose between isolation and a rollup.

**What changes.** Nothing already released breaks. Today's tenant-owned definitions stay as
tenant-local definitions. The site singleton (#885, #860) remains the one-site case until a tenant
needs a second site.

**What would make it wrong.** A deployment where units share data rather than processes. That is one
tenant with permissions, not several tenants.

## D26. Every setting, secret and operation is either per tenant or per deployment, never neither

**Decided:** 15 Sept 2026. **Status:** accepted. **Issue:** #936.

The data model is tenant first, and what surrounds it follows. Each setting, stored secret, backup,
export, log line, metric and domain says whether it belongs to a tenant or to the deployment. A
tenant value overrides a deployment default where both make sense.

- Tenant settings live in one tenant settings document with sections modules declare (#896).
- A tenant can be exported, imported, moved, archived and erased as one unit (#897).
- The config-as-code file (#345) has a deployment section and a tenants section, and the seeder applies
  that file (#905).
- Stored secrets carry a key id (#900); logs and metrics carry the tenant (#903); CORS and TLS come from
  the tenant domain map (#904).

**Why.** A hotel branch needs its own sender, bucket and restore without taking every other branch
with it (D25). Each of #345, #844, BaryoVM's recipes and #833 would otherwise assume the box.

## D27. The API owns the site model; barakoPress renders it

**Decided:** 15 Sept 2026. **Status:** accepted. **Issue:** #937. **Extends:** D20, D22.

The shape of a site is API data: the validated site schema (#924), each tenant's public origin and
route map (#923), mode and share sessions, block schema and presets, resolved block data (#926) and a
cache class per route (#922). barakoPress owns rendering primitives, theme and HTML. A renderer
registers the blocks it can render with the API; it does not hold rules, presets or bindings of its own.

**Why.** Otherwise "render it with barakoPress" becomes "render it only with barakoPress". Another
frontend, a mobile app or an offline shell reads the same site from the API.

## D28. A module declares itself once, and is enabled per tenant

**Decided:** 15 Sept 2026. **Status:** accepted. **Issue:** #938.

A module has one manifest: name, OpenAPI tag, capabilities with defaults, settings schema, exportable
documents, inbound receivers, triggers, console screen descriptor and renderer blocks. It is enabled
per tenant, seeds into each tenant that enables it, and has an uninstall hook that states what happens
to that tenant's data. `/api/modules` reports presence per tenant publicly and detail to admins. The
manifest is a descriptor, never a bundle of code for another product (#635). This moves the module
contract version.

**Why.** barakoBrew screens, barista commands, MCP tools and barakoPress blocks all need the same
declaration, and today a tenant created after startup never receives a module's roles or types.

## D29. The API is the only home of schema, rules and dry run

**Decided:** 15 Sept 2026. **Status:** accepted. **Issue:** #939.

Field types, validation rules, capabilities and workflow actions are described by the API (#931).
Every write accepts a dry run (#892). The OpenAPI document is committed and a change to it is checked
in CI (#631). One export, plan and apply engine replaces Portability's partial bundle (#921). API keys
are scoped by capability (#906). barakoBrew, barista, MCP and the VS Code extension are clients of
these and keep no copy of a rule.

**Why.** Each surface building its own validation and preview is the same mistake as a console-only
rule (D20), repeated per surface and per language.

## D30. A content type is a versioned schema

**Decided:** 15 Sept 2026. **Status:** accepted. **Issue:** #940.

A field is added, renamed, converted or removed through one endpoint. Each change is classified as
additive, rename, convert or destructive, recorded on the type, and applied to existing entries by a
job with run visibility (D8). A destructive change is refused unless it is confirmed. Field roles and
many-valued references land on `FieldDefinition` in the same change (#887, #928). Import goes through
the same validation (#933).

**Why.** Config as code, blueprints that evolve and live client sites all need to change a type that
already has data. Today the only route skips validation and drops the lifecycle.

## D31. A principal is an identity plus a membership per tenant

**Decided:** 15 Sept 2026. **Status:** accepted. **Issue:** #941. **Revisits:** the global roles note
in `docs/multi-tenancy.md`.

An identity is a person, an external identity link or a service. A membership in a tenant (or a group
of tenants, D25) carries roles, a profile and a security policy. Content and fields have an audience
level between Public and staff, and delivery accepts a viewer token checked by the same predicate
compiler authoring uses (#912). Roles are owned by a tenant or group, with a small set of platform
roles (#913). Signing in to the public site does not make someone a console user.

**Why.** Hotel guests, club members, a teacher clocking in and a partner integration are not console
users, and today they have to pretend to be one or stay anonymous.

## D32. Wolverine runs durable work, behind seams barakoCMS owns

**Decided:** 15 Sept 2026. **Status:** accepted, pending the spike in #687. **Supersedes:** the
substrate half of D19. D19's rule that a workflow definition is data still holds.

Durable work (jobs, retries, dead letters, scheduled triggers, waits, the outbox to shots and grinds,
and leader election) moves onto Wolverine with its Marten integration, underneath FastEndpoints. HTTP
does not change.

**The seams.** Core and modules depend on barakoCMS interfaces in `Core/Interfaces`, not on Wolverine
types:

- enqueue or publish a message inside the current transaction (the D15 property);
- schedule a message for a time;
- start, wait for and resume a run;
- handle a message type, as a plain class with no framework base type.

Wolverine implements those in `Infrastructure`. Retry policies, queues and leader election are
configured there and never reach a module. The seam is the handful of operations barakoCMS needs, not
a wrapper over all of Wolverine; wrapping everything would cost its features and still leak.

**Why.** Three hand-rolled mechanisms and three advisory locks already produced a day of bugs (#856),
and the next train needs schedules, waits and an outbox. Wolverine is MIT and already half here through
JasperFx. Owning the seams keeps the module contract (D18) free of a dependency we might replace, and
bounds any future swap to `Infrastructure`.

**What would make it wrong.** The spike in #687 failing on transactional enqueue, conjoined tenancy or
migrations. Then the seams stay and the implementation stays ours.

## D33. Content varies by language per field, not per entry

**Decided:** 15 Sept 2026. **Status:** accepted. **Issue:** #98.

One entry holds every language. A field is marked as varying by language or shared. Title, body and
slug vary; price, capacity, dates, images and references are stored once.

- **Publish state is per language.** English can be published while Filipino is a draft.
- **Slugs are per language,** unique per type per language.
- **A tenant sets its languages and a fallback chain,** for example `fil` falls back to `en`.
- **Delivery takes `?locale=`** and falls back through the chain, and the response says which language
  each field came from.
- **barakoBrew shows missing translations;** barista exports and imports strings for a translator.
- **The site entry follows the same rule,** so a site's name and footer text can vary by language.
- **Changing whether a field varies** is a schema change under D30, applied to existing entries by a job.

**Why.** A separate entry per language copies every shared field, and copies drift: a room's price
changed in English and not in Filipino. A translations table makes every read a join and every edit
two screens. Per-field variants keep shared data single and let each language publish on its own.

**What it costs.** Search keeps text per language, and a document grows with each language. For the
two or three languages a Philippine site runs, that is small.

## D34. Frameworks stay behind the contracts others build on, and are used directly inside core

**Decided:** 15 Sept 2026. **Status:** accepted. **Extends:** D18, D32.

What an outside author compiles against names barakoCMS types only. The module contract
(`Modules/*`, `Core/Interfaces/*`) and the client packages carry no Marten, FastEndpoints or Wolverine
types. Inside core, slices keep using `IDocumentSession`, FastEndpoints and Wolverine directly, as
section 1a of `CLAUDE.md` says.

- **Modules** get barakoCMS interfaces for what they need today from Marten: writing content
  (`IContentWriter`), reading content, a seed context and a hook context in place of
  `IContentLifecycleHook.Session` and the `IDocumentSession` in `IBarakoModule.SeedAsync`.
- **Endpoints stay thin.** An endpoint binds the request, calls a handler and maps the result to a
  status. Logic lives in the handler. The wire shape is pinned by the committed OpenAPI document (D29),
  so a change of HTTP framework is checked by CI rather than by reading.
- **Marten is not wrapped inside core.** Tenancy, the event store, projections and concurrency are the
  design, not an implementation detail, and a generic repository would give them up. Leaving Marten
  would rewrite persistence with or without a wrapper. The escape hatch is the data: it lives in
  Postgres tables, and the export engine and tenant bundle (D26, D29) move it out.

**Timing.** The new interfaces are added in 4.x with the Marten-typed members marked `[Obsolete]`, and
the old members go in 5.0.0, as section 6 requires. Endpoints thin out as each slice is touched.

**Why.** A Marten or FastEndpoints major upgrade should cost core a migration, not break every module
someone else wrote.
