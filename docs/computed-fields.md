# Computed fields

A computed field is a field whose value the server works out from other fields of the same entry.
The caller never sends it. A write that sets one is refused with a 400 saying the field is
computed by the server.

A rule is one of two shapes, and a field has one or the other, never both:

```json
{ "name": "Hours", "type": "decimal", "computed": { "fn": "hours", "args": ["TimeIn", "TimeOut"], "round": 2 } }
{ "name": "Total", "type": "decimal", "computed": { "expression": "Qty * Price", "round": 2 } }
```

A rule with both `fn` or `args` and an `expression` is refused when the type is saved.

## Functions

`fn` is one of `add`, `subtract`, `multiply`, `divide`, `round`, `min`, `max`, `hours`, `days`,
`duration`, `concat` and `coalesce`. `args` are field names of the same entry. A numeric function
also takes a number written out. `round` is the number of decimal places, 0 to 10.

An input that is missing or blank leaves the result out of the entry, except for `concat` and
`coalesce`, which skip it. A division by zero leaves the result out too.

## Formulas

`expression` is a formula in a closed language: operators, a fixed list of functions, and no code.
The full list of functions, operators and bounds is in [expressions.md](expressions.md).

A field is read by its name, `Qty * Price`, or as `data.Qty`. `round` on a formula rule applies
only when the formula gives a number.

The formula is checked when the type is saved. A save is refused (400, with the rule and the
position in the formula) when:

- the formula does not parse, or names a field the type does not have, or a field a formula cannot
  read. A formula reads `int`, `decimal`, `money`, `string`, `text`, `bool`, `date` and `datetime`
  fields. It cannot read any other type: `email`, `url`, `slug`, `uuid`, `richtext`, `markdown`,
  `choice`, `reference`, `geopoint`, `object`, `json`, `time`, `token` and array fields. `concat`
  in an `fn` rule still covers the text-like ones;
- the formula reads the clock (`today()`, `now()`), because the stored value would go stale;
- the formula reads `id`, `status`, `createdAt` or `updatedAt`, which are not part of the entry's
  data, so the value would go stale;
- the type of the result does not fit the field: a number needs `int`, `decimal` or `money`, text
  needs `string` or `text`, a bool needs `bool`, a date needs `date` or `datetime`;
- the field reads itself, or sits in a cycle with other computed fields;
- the field is required, or has a default value;
- the field is more widely readable than a field it reads.

The result is stored the way any value of that field type is. A number is stored as a decimal, and
an `int` field rounds to a whole number. A `date` result is stored as `yyyy-MM-dd`, and a
`datetime` result as ISO 8601 UTC with a trailing `Z`.

## When the value is worked out

Every path that writes an entry works the value out again from the entry's own fields:
create, update, rollback, bulk import, bundle import and collection sync, scheduled
publish, the field-change job, the workflow actions that create or update an entry, and the
sample data the server seeds. The tenant comes from the session, so two tenants with the same type
name use their own formulas.

A rollback is refused, as it is for `fn` rules, when the restored version carries a computed value
that differs from the one stored now. The server does not take a computed value from a caller or
from history.

When a formula gives no value (division by zero, a number out of range, a stored value of the
wrong type, a bound hit), the field is left out of the entry and one warning is logged with the
field name, the rule and the position in the formula. The log never holds an entry value or the
text of the formula.

## Changing a formula

Changing an `expression`, or adding a computed field with one, on a type that has entries starts a
background job that works the value out again for every entry. It is a field change of kind
`recompute`, and it follows the rules in [field-changes.md](field-changes.md#recomputing-a-formula).
A change to an `fn` rule does not start one. If the job gets stuck and the type stays write-locked,
an operator can abandon the recompute, see
[Abandoning a recompute](field-changes.md#abandoning-a-recompute). Entries the job had not reached
keep their old value until their next write.

A bundle import treats a changed `expression` as a change of computation, in the same way.

A type saved before formulas existed loads unchanged.

## Who can see a formula

A formula names fields, so its text is shown only to a reader who may read every field it names.
A reader who may not read one of them is shown the field without its `computed` rule, and the
public type description never serves the rule. A formula whose text does not parse counts as
reading every field. The value of a computed field follows the field's own sensitivity.

## Rollups

A rollup is a field on one type that summarises the entries of another type that point at it, such
as a teacher's total hours or an invoice's paid amount. The server keeps the value. A client never
sends it.

```json
{ "name": "HoursTotal", "type": "decimal",
  "rollup": { "from": "timeEntry", "via": "Teacher", "fn": "sum", "field": "Hours", "round": 2 } }
```

`from` is the type of the entries that are counted. `via` is a field of `from` holding a single
reference to the type that declares the rollup. `fn` is `count`, `sum`, `min`, `max` or `avg`.
`field` names the summed field of `from`, and is left out for `count`. `round` is 0 to 10 and
applies to `sum` and `avg`. Without it `avg` rounds to 2 places and `sum` keeps the exact value.

The declared type follows `fn`. `count` is `int`. `sum` has the type of the summed field (`int`,
`decimal` or `money`). `avg` is `decimal`, or `money` for a summed `money` field. `min` and `max`
have the type of the summed field, which may also be `date` or `datetime`. A `money` rollup carries
the summed field's currency. With nothing to count, `count` and `sum` are 0, and `min`, `max` and
`avg` are left out.

### Which entries count

An entry counts toward a header when its `via` value names the header, it is not archived, its own
sensitivity is not Hidden and is no stricter than the rollup's, and its status allows it. A
Public or Members rollup counts Published entries only. A Sensitive or Hidden rollup also counts
Draft and Scheduled ones. For `sum`, `min`, `max` and `avg`, an entry whose value is not a number
(or a valid date) is skipped, and it still counts toward `count`.

### Rules a type is saved under

A rollup is refused when `from` does not exist, `via` is not a single reference to this type, or
the summed field has a type `fn` cannot use. It cannot be required, have a default, be unique, be
computed, be a line total field, or be owner-hidden. Its level must be at least as strict as the
level of `field` and of `via`, even for `count`, because a count over a Sensitive reference would
publish how many entries point at each header. The saver must be able to read `field` and `via`.
Account-owned types do not take rollups. Neither do branch scoped types: a stored count covers the
entries of every branch, and a branch scoped type is read only within its branch.

A change to `from` that would break a rollup is refused with a 409 naming it: renaming or
removing `via` or `field`, changing their type, raising them above the rollup's level, changing the
summed currency, turning on branch scoping for `from`, or deleting `from`.

### Writes

A write that sends a rollup value is refused with a 400 when it differs from the stored one. A
write that leaves it out keeps the stored value. Restoring an old version keeps the current value.
Type permissions do not narrow a rollup: it counts every matching entry, whatever the reader may
read of `from`. The field's sensitivity is what protects it.

### Lag

A write to an entry in `from` marks the headers it affects in the same transaction. A background
job then recomputes the marked headers from the entries themselves, never from a change, so a
repeated run gives the same value. The value follows about a second after the write commits, and a
busy queue can delay it. If a write is lost between the commit and the job, the mark is still
stored and the job runs again.

The recompute writes the header through the same path as a field change, so the header's version
moves and an `If-Match` taken before the change gets a 412. History records the change, the
header's own update time is untouched, and no workflow starts.

### Cost

Each recompute scans the tenant's entries of `from` once per rollup field, in batches of up to 100
headers. There is no index for it yet. A very large `from` type should wait for one.

### Adding or changing a rollup

Adding a rollup to a type with entries, or changing its `from`, `via`, `fn`, `field`, `round` or
level (raised or kept), starts a background field change of kind `recompute`, as a formula does. Status, abandon and
resume work the same way. The header type is write-locked while it runs, and marks for it are held
until it ends.

Changing the level of a rollup field with the field sensitivity endpoint starts no second change. The
change gains a step that recounts each header at the new level. Lowering the level recounts first,
then changes the definition, then rebuilds the search text, so readers only ever see a count of
fewer entries than the old level counted. Raising the level rebuilds the search text, changes the
definition, then recounts, so the count that includes Sensitive entries and drafts is written only
once readers are held to the new level. A config apply or import
that changes the level of a rollup is refused, as it is for any field, and points to the sensitivity
endpoint.

A write to `from` whose transaction read the definitions before a new rollup was saved, and
commits after the backfill passed its header, is not counted until the next write to that header's
entries. Field changes carry the same window.
