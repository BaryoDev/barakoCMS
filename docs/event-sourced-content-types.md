# Event-sourced content types

This page is for the person creating a content type, in barakoBrew or through the API, and deciding whether to turn
event sourcing on. It explains what the toggle commits you to, in the order the surprises would
otherwise arrive.

## Where the toggle is

`eventSourced` on `POST /api/content-types`, and it defaults to false. The design record is
`EVENT-SOURCING-PER-CONTENT-TYPE.md`.

## What the choice does

Every content type keeps a full change log either way. What event sourcing changes is which copy of
the truth is the real one.

- **Off** (the default): the current version of each item is the record. History exists for
  reference, but the current version does not depend on it.
- **On**: the history is the record. The current version is derived from it and can be deleted and
  rebuilt from history at any time, including its sensitivity and scheduling settings. Two
  timestamps shift by the write latency, which the rebuild section below explains. That rebuild is
  the whole point: it is what makes the history trustworthy for audit rather than merely
  informative.

One difference is visible to people editing content. If two people open the same item and both
save, an event-sourced type rejects the second save with a conflict (HTTP 409), because it was
based on a version that is no longer current. Types without the flag keep today's behaviour, where
the second save silently wins. A rejected save is the feature working, not an error in the system.

## The choice is permanent

You choose when the content type is created, and the choice cannot be changed afterwards, in
either direction. Turning it on later is impossible because the history to rebuild from was never
recorded. Turning it off later would throw away a record that people may be relying on.

Deleting the content type and creating a new one with the same name does not reset it. The choice
is stored against the type name, separately from the type itself, and is written once. A recreated
name inherits the original decision. This is deliberate: without it, delete-and-recreate would be a
way around a promise the system made about your data.

There is one more thing the choice cannot survive: entries. A type name that already has content
written under the default cannot be recreated as event sourced. Those entries have a stream behind
them, but it was written while the document was the record, and entries older than 4.0 carry no
sensitivity setting at all, so a rebuild would produce items that look right and are readable by
people who should not see them. Event sourcing has to be chosen before the first entry.

So treat the toggle like a decision, not a setting. If you are unsure, leave it off. Off is the
default and matches how every type behaves today.

## Personal data is refused

An event-sourced type will not accept fields marked anything other than Public. Creating one with a
Sensitive or Hidden field is refused, and so is raising a field to either level later.

The reason is a direct conflict. An event-sourced type's value is a history that is never altered.
The right to erasure is an obligation to remove personal data on request. In this project erasure
means deletion (decision D9 in `DECISIONS.md`, and `compliance-posture.md`): the item, its history
and its current version are removed together, because anything less is not erasure. Deleting
history out of a type whose entire point is a complete history destroys what you chose the flag
for. Refusing the combination up front means you find out at creation time, not during a
data-subject request.

One honest limit: the refusal is keyed to how fields are marked, and a Public field can still hold
a name. It is a mitigation, not a guarantee. If a type will hold personal data, do not event-source
it, whatever its fields are marked.

## Two kinds of types in one API

Once any type is event-sourced, the same API holds types with two behaviours: event-sourced types
reject stale saves with a 409, the rest keep last-write-wins. That is the direction of travel, not
an inconsistency awaiting cleanup. Moving a type from last-write-wins to conflict rejection later
would break clients that never handled a conflict, while the reverse breaks nothing, so new
semantics arrive only with new event-sourced types and existing types keep behaving as they always
have. Anyone building against the API should handle 409 on writes to event-sourced types.

## Rebuilding

`POST /api/content-types/{name}/rebuild` (Admin or SuperAdmin) throws the current version of every
item of the type away and produces it again from the history. It is refused for a type that is not
event-sourced, whose current version is the record and whose history is only a reference copy:
replaying over it would be an overwrite dressed as a repair.

Two timestamps do not come back exactly. Created and updated times are taken from the history
entries, which the database stamps at the moment of the write, while the live path stamps them as
the change is applied. The two are close and never identical, so a rebuild shifts both by the write
latency.

## Costs worth knowing before choosing

Every edit is kept forever, so a type edited hundreds of times a day is a poor candidate: its
history grows faster than its value. And rebuilding from history takes time that grows with the
history, so on a large type it becomes a scheduled operation rather than an instant one. Each write
also does slightly more work, since the history entry and the current version are saved together in
one transaction.

If none of the above made you want the flag, you do not need it. The default already keeps history
for reference and audit. The flag is for types where the history must be the record.

## Turning off history for ordinary types

A content type that is not event-sourced still writes every change to its history. That is what
makes `GET /api/contents/{id}/history` and the rollback endpoint work for every type rather than
only for the event-sourced ones, and it is the default.

`EventSourcing:DocumentTypesAppend` turns it off:

```json
{
  "EventSourcing": {
    "DocumentTypesAppend": false
  }
}
```

Omit it and it is on, which is what every deployment before 4.0 did.

Read the cost before setting it. With it off, a type that is not event-sourced writes nothing to
its history, so for those types:

- `GET /api/contents/{id}/history` returns nothing, for entries created from that point on.
- The rollback endpoint has nothing to roll back to.
- **Workflows stop firing.** Workflows are triggered by reading committed created, updated and
  published entries out of the history. No history entry means no trigger, and nothing reports an
  error: the save succeeds and the workflow simply never runs.

The setting does not reach an event-sourced type. For those the history is the record, and no
setting takes that away.

Turn it off if you run content types nobody audits, nobody rolls back and no workflow watches, and
you want the storage back. Leave it alone otherwise.
