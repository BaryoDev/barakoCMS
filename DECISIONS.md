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
