# Opt-in event sourcing, per content type

A design plan. **Steps 1 and 2 below are implemented**; steps 3 to 5 are not, and the open decisions
at the end are settled in `DECISIONS.md` rather than here.

What has landed: every content event now carries the fields a rebuild needs, including `Sensitivity`,
`ContentScheduled` and `ContentSensitivityChanged`; and every write path goes through
`IContentWriter`. The prerequisite gap described under "the events are not complete enough yet" is
therefore closed, and the section is kept because the reasoning still explains why the shape is what
it is.

## What is being proposed

A content type declares once, at creation, whether its content is event sourced. That choice cannot
be changed afterwards.

- **Not event sourced** (today's behaviour, and the default): the `Content` document is the source of
  truth. Events are still emitted for workflow, integration and audit, but nothing rebuilds state
  from them.
- **Event sourced**: the stream is the source of truth. The `Content` document still exists and is
  still what everything reads, but it is produced by a projection and can be discarded and rebuilt.

Per content type, not per document. A per-document choice would mean two documents of the same type
answering the same query with different consistency guarantees, and no way to reason about a type as
a whole.

## The idea that keeps this small

**The read model does not change. Only its provenance does.**

Everything downstream reads `Content` documents: the Delivery API, the admin UI, search and
`SearchText`, the sitemap, public feeds. If event-sourced types produced a different shape, or no
document at all, every one of those forks into two code paths.

So in both modes a `Content` document is written, in the same transaction, with the same shape. The
difference is who owns it:

| | Source of truth | Who writes the document | Can it be rebuilt? |
| :--- | :--- | :--- | :--- |
| Default | the document | the write handler | no |
| Event sourced | the stream | an inline projection | yes |

That last column is the whole feature. If the document cannot be deleted and rebuilt from the
stream, the type is not event sourced, whatever the flag says.

---

## Prerequisite: the events are not complete enough yet

**This is a gate, not a task.** Per-type event sourcing cannot ship until it is closed, because
flipping a type today would silently lose data on the first rebuild.

### Three fields never reach an event

`Content` has 11 properties. Three of them are on no event at all:

| Field | Carried by |
| :--- | :--- |
| `Id`, `ContentType`, `Data`, `Status`, `SearchText` | `ContentCreated` |
| `Data`, `SearchText` | `ContentUpdated` |
| `Status` | `ContentStatusChanged` |
| `CreatedAt`, `UpdatedAt` | derivable from the event timestamp |
| `LastModifiedBy` | `CreatedBy` / `UpdatedBy` |
| **`Sensitivity`** | **nothing** |
| **`ScheduledPublishAt`** | **nothing** |
| **`ScheduledUnpublishAt`** | **nothing** |

`Sensitivity` is the serious one. It drives field-level redaction, so a rebuild that loses it does
not produce a broken record, it produces a record that looks fine and is readable by people who
should not see it. That is a security regression that no test asserting "the document came back"
would catch.

### Two write paths emit no event at all

| Path | What it writes | Emits an event? |
| :--- | :--- | :--- |
| `Content/Create/Endpoint.cs` | the document | yes |
| `Content/Update/Endpoint.cs` | the document | yes |
| `Content/ChangeStatus/Endpoint.cs` | the document | yes |
| `Content/History/RollbackEndpoint.cs` | the document | yes |
| **`Content/Schedule/Endpoint.cs`** | the document | **no** |
| **`Workflows/Actions/CreateTaskAction.cs`** | a whole new document | **no** |

The workflow action is the more dangerous of the two, because it creates content rather than
amending it. An event-sourced type whose documents can be created by a path that appends nothing has
streams that do not exist for documents that do.

### What closing the gate requires

1. `ContentScheduled` and `ContentSensitivityChanged` events, or additional fields on the existing
   ones with upcasters for the old shapes.
2. Every one of the six write paths appends. Enforced by a test that fails when a new write path is
   added without one, not by review.
3. A test that maps every settable property of `Content` to the event that carries it, and fails when
   a property is added without one. Same shape as the descriptor-property-count guard in the Mapsicle
   work: it exists to fail on a future change, not to describe today.

---

## Where the decision lives

**One place.** The routing decision must not appear in six endpoints.

That is the exact failure the Mapsicle conversion cascade had: the same rule written three times, and
the copies drifted until two defects lived in all of them and a third lived in one. Six copies of
"is this type event sourced" would drift the same way, and the drift would be invisible because both
branches produce a valid-looking document.

```text
Features/Content/*/Endpoint.cs   ─┐
Workflows/Actions/*.cs           ─┼─→  IContentWriter  ─┬─→  document mode: Store(content), append event
                                  ┘                     └─→  stream mode:  Append(event) → inline projection
```

`CLAUDE.md` says not to add a service reflexively. This one is justified on its own terms: the logic
is genuinely shared across six call sites and complex enough to test alone.

The endpoints keep their slice structure and lose the persistence detail. They ask for a content
change to be recorded; they do not decide how.

### The projection

A Marten `SingleStreamProjection<Content>`, stream identified by the content `Id`.

**Inline, not async.** This is effectively forced rather than chosen:

- The admin UI saves and immediately reads. Async projection means read-after-write staleness, which
  presents as "my edit did not save" to an editor.
- An inline projection failure fails the write, which is what you want: better a failed save than a
  stream and a document that disagree.
- The async daemon is registered `Solo`, which processes on a single node. A multi-instance
  deployment would need `HotCold`, so async is not currently available at scale anyway.

The cost is that every write now does two things in one transaction. That is the price of the
feature and it should be stated in the docs, not discovered.

---

## Constraints

These are the things that make the feature harder than it looks. Each one needs a decision before
implementation, not during.

### 1. The flag is immutable, and immutability must be enforced

Not documented. Enforced, with a test.

**Why it cannot be turned on later.** There is no history to rebuild from. You would have to
synthesise a genesis event from the current document, and the stream would then assert a history that
did not happen. Every audit answer derived from it would be wrong in a way nobody can detect.

**Why it cannot be turned off later.** The stream is the record. Turning it off discards history that
callers may be relying on for audit or compliance, and the discarding is unrecoverable.

Enforcement:
- The content type update endpoint rejects any request that changes the flag, with a message that
  says why rather than "invalid request".
- A test asserts the rejection, and a paired test asserts that an update which does not touch the
  flag still succeeds. Otherwise "reject everything" passes.

### 2. Delete-and-recreate is a hole in that immutability

Nothing stops someone deleting a content type and recreating it with the same name and the opposite
flag. Existing documents and streams then belong to a type whose rules have changed.

Decide one of:
- Content type names are never reusable after deletion.
- Deleting a content type with existing content is refused.
- Recreation inherits the original flag, which means deletion is a soft delete that remembers it.

This needs answering. It is the obvious way around the constraint and someone will find it.

### 3. Right to erasure conflicts with an immutable stream

A CMS stores user-defined schemas. Those will contain personal data, whatever the documentation
suggests. An immutable event stream and a legal obligation to erase are in direct conflict.

Options, in increasing cost:
- **Document the limitation.** Event-sourced types are unsuitable for personal data. Cheapest, and it
  will be ignored by someone.
- **Tombstone and rebuild.** Erasure appends a redaction event and rebuilds; the payload remains in
  earlier events, so this is not true erasure.
- **Crypto-shredding.** Event payloads encrypted per subject; erasure throws away the key. Real
  erasure, and the largest change.

**This must be decided before the first person stores personal data in an event-sourced type**, not
after. It is not reversible once data exists.

### 4. Event schema evolution becomes load-bearing

While the document is authoritative, an event shape change is cosmetic. Once the stream is the
source of truth, an event shape change breaks rebuild for every stream ever written.

`ContentCreated` and `ContentUpdated` already carry `[Obsolete]` constructors, so the shape is
already moving. Upcasters are required from the first event-sourced type, not added when it first
hurts.

### 5. Storage grows without bound, and rebuild time grows with it

Every update appends forever. There is no compaction without snapshotting. Two consequences worth
stating in the documentation rather than leaving to be discovered:

- A high-churn content type is a poor candidate. A type edited hundreds of times a day accumulates
  faster than its value justifies.
- Rebuild is an operation with a duration. At some stream count it stops being something that fits in
  a deploy window, and that point arrives without warning.

Snapshotting is the mitigation, and it is a later feature, not part of this one.

### 6. Tenancy correctness is a security property here

`Events.TenancyStyle` is `Conjoined`, so events are tenant-partitioned. A rebuild must therefore
rebuild per tenant and must not read across tenants.

A rebuild that crossed tenants would write one tenant's content into another's documents, which is a
data breach rather than a bug. This needs an explicit test with two tenants, not an assumption that
Marten handles it.

### 7. Concurrency semantics differ between the two modes

Document mode is last-write-wins. Stream mode can use Marten's expected-version checking to reject a
write based on a stale read.

That is a behaviour difference between two types in the same API, so decide deliberately:
- Match document mode, and accept last-write-wins for consistency of behaviour.
- Use expected-version, and accept that event-sourced types can return a conflict that other types
  never return.

Either is defensible. What is not defensible is arriving at one by accident.

### 8. The workflow action is easy to forget

`CreateTaskAction` creates content outside the Content feature slices. It has to go through the same
writer. A test should create content through a workflow action against an event-sourced type and
assert a stream exists.

---

## How you know it works

One test decides whether the feature is real:

1. Create content of an event-sourced type. Update it, change its status, schedule it, set its
   sensitivity.
2. Delete every `Content` document for that type, leaving only the streams.
3. Rebuild the projection.
4. Assert the documents come back **identical across all eleven properties**, including `Sensitivity`
   and both scheduling fields.

If that fails, the type is not event sourced regardless of the flag.

Then the paired control, so the assertion is not vacuous: run the same test against a
**non**-event-sourced type and assert the documents do **not** come back. A rebuild that appears to
restore both modes means something else is writing the documents and the test is proving nothing.

Also worth having:
- Every settable property of `Content` is carried by some event. Fails when a property is added.
- Every write path appends. Fails when a write path is added.
- The flag cannot be changed, with a paired test that unrelated updates still succeed.
- Two tenants, rebuild, no crossing.

---

## Suggested order

Each step is shippable and leaves the system working.

1. **Close the event gap.** The three fields and the two silent write paths, plus the two guard tests.
   Ships on its own merit: it fixes the audit trail whether or not this feature happens.
2. **Route every write through one writer**, still document mode only. No behaviour change, and it
   should be provable by the existing suite staying green.
3. **Add the flag**, defaulting to false, immutable, with the rejection test. Still nothing reads it.
4. **Add the projection and the stream path**, gated behind the flag. Now the acceptance test above
   can be written.
5. **Documentation**: what the choice means, that it is permanent, and the personal-data limitation.

Steps 1 and 2 are worth doing regardless of whether this feature ships, which makes them safe to
start before the constraints above are all decided.

---

## Open decisions

All four are answered. They were one-way doors, which is why they were listed here rather than left
to the implementation.

1. **Delete-and-recreate of a content type: the original flag is inherited.** The policy is keyed by
   the type name and written once, so recreating a name cannot arrive at the opposite answer.
   Decided in #230.
2. **Personal data: refuse the combination.** An event-sourced type may not hold non-Public fields.
   Decided in #230, and consistent with D9 in `DECISIONS.md`, where erasure is a delete rather than
   a tombstone or a shred.
3. **Concurrency: expected-version with a 409, for event-sourced types only.** Decided in #230.
4. **The stream is internal. An event-sourced type does not expose its history through the API.**
   Decided 30 Aug 2026, before #331.

### On decision 4

Exposing the history would make every event record a public type under CLAUDE.md section 6, frozen
until the next major. That is a large permanent commitment bought for a feature nobody has asked
for, and it would be paid by the people least able to see the bill: whoever next needs to add a
field to `ContentUpdated`.

It is also the reversible direction. Adding a history endpoint later is additive and breaks nothing.
Removing one, once clients read it, is a major-version event. So the question is not which answer is
better in the abstract, it is which answer can still be changed after it turns out to be wrong, and
only one of them can.

There is already a `GET /api/contents/{id}/history` in document mode, built on `AuditEvent` rather
than on the stream. That stays as it is. The distinction matters and should be kept clear in the
docs: the audit trail is a record of who did what, and it is a separate thing from the event stream
that an event-sourced type is rebuilt from. Backing the existing endpoint with the stream instead
would be exactly the exposure this decision refuses.
