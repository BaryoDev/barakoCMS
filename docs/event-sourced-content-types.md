# Event-sourced content types

This page is for the person creating a content type in the admin UI and deciding whether to turn
event sourcing on. It explains what the toggle commits you to, in the order the surprises would
otherwise arrive.

## Not available yet

**The toggle does not exist today.** How it will behave is decided (issues #230 and #331, and the
design record in `EVENT-SOURCING-PER-CONTENT-TYPE.md`), but neither has shipped. This page is
published ahead of the feature so the commitments are readable before anyone can make them. If you
can see the toggle and this notice is still here, ask before using it.

## What the choice does

Every content type keeps a full change log either way. What event sourcing changes is which copy of
the truth is the real one.

- **Off** (the default): the current version of each item is the record. History exists for
  reference, but the current version does not depend on it.
- **On**: the history is the record. The current version is derived from it and can be deleted and
  rebuilt from history at any time, exactly as it was, including its sensitivity and scheduling
  settings. That rebuild is the whole point: it is what makes the history trustworthy for audit
  rather than merely informative.

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

So treat the toggle like a decision, not a setting. If you are unsure, leave it off. Off is the
default and matches how every type behaves today.

## Personal data is refused

An event-sourced type will not accept fields marked anything other than Public. Creating one with a
Sensitive or Internal field is refused, and so is adding such a field later.

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

## Costs worth knowing before choosing

Every edit is kept forever, so a type edited hundreds of times a day is a poor candidate: its
history grows faster than its value. And rebuilding from history takes time that grows with the
history, so on a large type it becomes a scheduled operation rather than an instant one. Each write
also does slightly more work, since the history entry and the current version are saved together in
one transaction.

If none of the above made you want the flag, you do not need it. The default already keeps history
for reference and audit. The flag is for types where the history must be the record.
