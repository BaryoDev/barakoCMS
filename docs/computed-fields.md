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
