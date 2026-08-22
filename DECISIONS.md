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

**Decided:** 22 Aug 2026. **Issue:** #229. **Status:** accepted, guard test not yet written.

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
