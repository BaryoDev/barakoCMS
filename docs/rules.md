# Rules

A content type can declare rules. A rule checks an entry when it is saved or when it moves through
a lifecycle transition. If a condition does not hold, the write is refused with 422. If every
condition holds, the rule can also set fields on the entry.

## Where a rule lives

Rules are the `Rules` list on the content type (`ContentTypeDefinition.Rules`). Send it with the
rest of the type when you create or update the type. `When` is `Save` or `Transition`.

```json
{
  "name": "Receipt",
  "rules": [
    {
      "name": "needs-order",
      "when": "Save",
      "all": [ { "kind": "field", "field": "Order", "op": "notEmpty" } ],
      "message": "A receipt needs a order."
    }
  ]
}
```

| Property | Meaning |
| :--- | :--- |
| `name` | Reported in the default refusal text. |
| `when` | `Save` (create and update, the default) or `Transition`. |
| `transition` | For `Transition` rules: only this transition (ignoring case). Null means every transition. |
| `inState` | Only when the entry is in this state (ignoring case). On a save it is the stored entry's state, so it never matches a create. On a transition it is the state being left. Null means any state. |
| `timeZone` | IANA zone for `$NOW` and `$TODAY`. |
| `if` | Preconditions. See [Preconditions](#preconditions-and-messages). |
| `let` | Named values worked out first. See [Lets](#lets). |
| `all` | Conditions that must all hold. |
| `set` | Fields written when the rule passes, by template. |
| `message` | The refusal text. |

Rules run in the order they are declared. The first one that refuses answers. Inside one rule the
order is `if`, then `let`, then `all`, then `set`. If a rule has no `message` and nothing more
specific to say, the refusal is `Rule '<name>' failed.`

## Order of checks

Create (`POST /api/contents`), after the create permission check:

1. Write sensitivity (fields the caller may not see are dropped).
2. The create permission rule, asked of what is left (403 if it does not hold).
3. Rules.
4. Schema validation.
5. Lifecycle hooks.
6. If the create permission rule holds for the entry as it will be stored, the write goes ahead (or a dry run answers). Otherwise 403, and this check answers before any refusal from steps 3 to 5.

Rules run before validation so a field a rule sets is validated and seen by the hooks.

Update (`PUT /api/contents/{id}`):

1. The update permission.
2. Write sensitivity.
3. The update permission rule, asked of the data as it will be stored (403 if it does not hold).
4. Save rules, run on the data as it will be stored. An update replaces the data whole.
5. If a rule set a field, the update permission rule is asked again of the entry those fields leave behind (403 if it does not hold).
6. Schema validation.
7. Lifecycle hooks.
8. Dry run, if asked for, else the write.

Transition (`PUT /api/contents/{id}/status` with `transition`):

1. If the caller sent fields, they are matched to the fields the transition takes, write sensitivity is applied, and the transition permission rule is asked of the entry they leave behind (403).
2. Fields the transition requires but did not get are refused with 400.
3. Transition rules, after the version check, so a stale move answers 409 and not a rule's refusal. The rules see the sent values laid over the stored entry, so a move that sends nothing is still checked against the stored fields.
4. If a rule set a field, the transition permission is asked again of the entry those fields leave behind (403).
5. Schema validation of the resulting data.

## A refusal

A rule that refuses a create, an update or a transition answers 422. The body has one `errors`
entry named `rules`. The reason is the condition's `message`, else the rule's `message`, else
`Rule '<name>' failed.`

```json
{ "errors": [ { "name": "rules", "reason": "A code is required." } ] }
```

On a create, a rule may read other entries, so its refusal, and any refusal after it, goes only
to a caller whose create permission rule holds for the entry. Any other caller gets 403. On an update the same holds
through step 3 above: a caller who fails the update permission rule never reaches the rules.

An error in the rule itself (an unknown kind, a lookup that is not registered, a bad expression)
also refuses the write with 422 and a text saying what is wrong, for example
`A rule uses an unknown condition kind 'x'.`

## Conditions

A condition has a `kind` (default `field`), and depending on the kind a `field`, an `op` (default
`eq`) and a `value`.

```json
{ "kind": "field", "field": "Status", "op": "in", "value": "Open,Paid" }
```

Common properties on any condition:

| Property | Meaning |
| :--- | :--- |
| `negate` | Inverts the result. |
| `if` | Guards. See [Preconditions](#preconditions-and-messages). |
| `message` | Replaces the rule's message when this condition is the one that fails. |

### Ops

| Op | Holds when |
| :--- | :--- |
| `eq` | The two sides are equal, ignoring case. |
| `ne` | They are not equal, ignoring case. |
| `gt`, `gte`, `lt`, `lte` | Ordered. Both sides numbers: compared as numbers. Both dates: compared as instants (no offset means UTC). Otherwise both text: compared ordinally. A number or a date against something that is not one has no order, so the condition does not hold. |
| `in` | The left side is in the comma list on the right (ignoring case). |
| `empty` | The left side is empty. |
| `notEmpty` | The left side is not empty. |

A missing value reads as empty. Numbers have a sign and a decimal point only: no thousands
separators, exponents or spaces. Op names are not case sensitive. An unknown op does not hold.

### Kinds

| Kind | What it checks |
| :--- | :--- |
| `field` | The field's value (or the `ref` entry's) against `op` and `value`. |
| `length` | The length of the field's text against `op` and `value`. |
| `pattern` | The field's text matches the regex in `value`. |
| `each` | Every item of an array or map field passes the conditions in `each`. |
| `itemCount` | The number of items in an array or map field against `op` and `value`. |
| `state` | The lifecycle state against `op` and `value`. |
| `changed` | The field's incoming text differs from the stored text. |
| `unique` | No other entry of this type has the same value in `field`. |
| `related` | At least one entry of `contentType` matches `where` and `state`, or the `ref` entry exists. |
| `count` | The number of entries matching `contentType`, `where` and `state`, against `op` and `value`. |
| `sum` | The sum of `sum` over matching entries, against `op` and `value`. |
| `lookup` | A registered [lookup](#lookups). |
| `stored` | The stored entry's value at `field` against `op` and `value`. |
| `member` | Every wanted value is in the allowed set. |
| `covers` | Every required value is among the field's values. |
| `distinct` | The field's values have no repeats. |
| `notRequester` | Provided by the Approvals module. Core refuses with `Condition kind 'notRequester' is provided by the Approvals module.` |

An unknown kind refuses with `A rule uses an unknown condition kind '<kind>'.`

## Field-level kinds

`length`, `pattern`, `each`, `itemCount`, `state` and `changed` read the entry being written.

```json
{ "kind": "length", "field": "Code", "op": "lte", "value": "8" }
```

`pattern` uses a .NET regex in `value`. A field with no text passes. The match times out after
250 ms, and a timeout refuses with `Pattern '<value>' on '<field>' timed out.` An invalid regex
refuses with `Pattern '<value>' on '<field>' is not a valid regex.`

```json
{ "kind": "pattern", "field": "Code", "value": "^[A-Z]{3}[0-9]{4}$" }
```

`each` runs its inner conditions on every item. An item that is an object is read by its keys. A
plain value is read as `$value`. An item of an object map also has `$key`. `parent.X` reads a field
of the entry that holds the list. If an inner condition fails and has a `message`, that message
(filled for the item) is the refusal. A missing field is an empty list, so `each` passes. A field
that is not an array or a map refuses with `Field '<field>' is not an array.`

```json
{
  "kind": "each", "field": "Lines",
  "each": [ { "kind": "field", "field": "Qty", "op": "lte", "value": "{{parent.Max}}",
              "message": "{{Name}} is over the limit." } ]
}
```

`itemCount` counts the same items, so it also works on an object map:

```json
{ "kind": "itemCount", "field": "Rates", "op": "gte", "value": "1" }
```

For a map, the inner conditions of `each` can check the keys:

```json
{ "kind": "each", "field": "Rates", "each": [ { "kind": "pattern", "field": "$key", "value": "^[A-Z0-9]+$" } ] }
```

`state` reads the lifecycle state the entry is leaving (the stored state on an update, the `from`
state on a transition, empty on a create), or the `ref` entry's state.

```json
{ "kind": "state", "op": "in", "value": "Draft,Open" }
```

`changed` holds when the field's text differs from the stored text. With no stored entry (a
create) it holds when the field is not empty. It ignores `op`.

```json
{ "kind": "changed", "field": "Amount", "negate": true, "if": [ { "kind": "state", "op": "eq", "value": "Posted" } ],
  "message": "A posted receipt's amount cannot change." }
```

## Paths

A `field` is a path into the entry. Names are matched ignoring case.

| Path | Reads |
| :--- | :--- |
| `A.B` | Field `B` of object `A`. |
| `A[last]` | The last item of list `A`. |
| `A[0]` | The item at that index. |
| `A[Field=value]` | Items whose `Field` equals `value` (ignoring case). |
| `A.$keys` | The keys of object map `A`. |
| `parent.X` | Inside `each`, field `X` of the entry that holds the list. |
| `let.x` | The value of the [let](#lets) named `x`. |

A list in the middle of a path is spread, so `Lines.Amount` reads every line's `Amount`.

```json
{ "kind": "field", "field": "Results[last].Status", "op": "eq", "value": "Final" }
```

## Templates

`message`, `where`, `ref`, `of`, `args` and `set` values can hold `{{...}}` templates. So can
`value` on the kinds `field`, `stored`, `member` and on a `value` let. On `length`, `pattern`,
`itemCount`, `state`, `count`, `sum` and `lookup`, `value` is read as written; to compare those
with a field, work the number out in a `let` and compare it with a `field` condition.
`{{Field}}` and any path above
fills with the field's text, or empty if it is missing. `$CURRENT_USER` fills with the caller's id.

Comparing a field with another field:

```json
{ "kind": "field", "field": "Paid", "op": "lte", "value": "{{CashAmount}}" }
```

`set` writes each template into its field when the rule passes:

```json
{ "name": "stamp", "all": [], "set": { "Total": "{{= Qty * Price }}", "Currency": "{{lookup.price.text}}" } }
```

A `set` template that names a lookup with no value refuses with
`Rule '<name>' could not fill '<field>': <reference> has no value.` A field is written only when
its value is not null and differs from what was sent.

### Lookup values

`{{lookup.<name>.<number|text|found>}}` fills from a lookup that a `lookup` condition or a
`lookup` let called earlier in the same rule. `number` and `text` fill empty when the lookup
returned none. `found` is `true` or `false`. The name is the part between `lookup.` and the last
dot, so `{{lookup.price.lookup.number}}` reads the lookup named `price.lookup`. Any other part
leaves the reference unresolved.

## Clock tokens

`$NOW` fills with the current instant in UTC (ISO 8601). `$TODAY` fills with today's date
(`yyyy-MM-dd`) in the rule's zone. An offset follows the token: `+` or `-`, one to four digits and
a unit, `y`, `mo`, `d`, `h` or `m`. The offset is added in the zone's local time.

```json
{ "kind": "field", "field": "ExpiresOn", "op": "lte", "value": "$NOW+2y",
  "message": "An expiry cannot be more than two years away." }
```

The zone is the rule's `timeZone`, else the site's `TimeZone` setting (the oldest published site
entry), else UTC. The site is read only for a rule that uses a clock token. A `timeZone` the server
does not know refuses with `Time zone '<id>' is not known on this server.` Dates are ordered as
instants, so `2026-10-16T08:00:00+08:00` and `2026-10-16T00:00:00Z` are equal.

## Preconditions and messages

A rule's `if` is a list of conditions. If any one does not hold, the rule is skipped and passes.

```json
{ "name": "paid-in-cash", "if": [ { "kind": "field", "field": "Method", "value": "Cash" } ],
  "all": [ { "kind": "field", "field": "CashAmount", "op": "notEmpty" } ], "message": "Enter the cash amount." }
```

A condition's own `if` works the same way: if a guard does not hold, the condition counts as met.
A condition's `message` replaces the rule's `message` when that condition is the one that fails.
Messages are templates:

```json
{ "kind": "field", "field": "Qty", "op": "gt", "value": "0", "message": "{{Name}}: quantity must be above 0." }
```

## Lets

A `let` works out a value once and names it with `as`. Lets run in order after `if` and before
`all`, and later lets can read earlier ones. Read one as `{{let.x}}` or as the path `let.x`.

| Kind | Value |
| :--- | :--- |
| `sum` | The sum, as for the `sum` condition. |
| `count` | The number of matching entries. |
| `field` | The field's text (of the `ref` entry when one is named). |
| `lookup` | The lookup's number, else its text, else nothing. |
| `value` | The filled `value` template. |

A let with no `as` refuses with `A let needs a name in 'as'.` Any other kind refuses with
`Let '<as>' cannot use kind '<kind>'.`

```json
{
  "name": "not-overpaid",
  "let": [ { "kind": "sum", "contentType": "Payment", "where": "Receipt={{Number}}", "sum": "Amount", "as": "paid" } ],
  "all": [ { "kind": "field", "field": "Total", "op": "gte", "value": "{{let.paid}}" } ],
  "message": "Payments of {{let.paid}} are over the total."
}
```

## Expressions

`{{= expr }}` works out arithmetic. It takes numbers, `'text'`, paths, `+ - * /`, brackets and
these functions:

| Function | Result |
| :--- | :--- |
| `round(x)`, `round(x, places)` | Rounds away from zero. `places` is a whole number from 0 to 28. |
| `min(a, b, ...)`, `max(a, b, ...)` | The smallest or largest. |
| `sum(path)` | The sum of every value at the path. |
| `ifeq(a, b, then, else)` | `then` if `a` equals `b` (ignoring case), else `else`. |

```json
{ "set": { "Total": "{{= round(sum(Lines.Amount) * 1.12, 2) }}", "Tier": "{{= ifeq(Type, 'VIP', 'A', 'B') }}" } }
```

An empty value counts as 0. A failed expression refuses the write with `Rule '<name>': <text>`,
where the text is one of:

| Text | Cause |
| :--- | :--- |
| `'<expr>' has an unexpected '<c>'.` | A character the grammar does not take. |
| `'<expr>' ends too early.` | The expression stops where a value is needed. |
| `'<expr>' divides by zero.` | A division by 0. |
| `'<expr>' nests too deeply.` | More than 64 levels. |
| `'<expr>' gives a number too large to hold.` | Overflow. |
| `'<expr>': '<value>' is not a number.` | A value that is not a number in arithmetic. |
| `'<expr>' calls an unknown function '<name>' or gives it the wrong number of values.` | Unknown name or wrong argument count. |
| `'<expr>' rounds to '<value>' places; use a whole number from 0 to 28.` | Bad `places`. |
| `'<expr>' has a quote that is not closed.` | A `'` with no closing `'`. |
| `'<expr>' is missing '<c>'.` | A missing `)`. |

## Reading other entries

These kinds read entries of `contentType` (this entry's own type when it is left out).

`where` filters by field values: `Field=value;Field2={{x}}`. Values are filled as templates and
compared ignoring case. A field name may hold only letters, digits and `_`, or the rule refuses
with `Field '<name>' is not a valid field name.` `state` is a comma list of lifecycle states. A
scan over more than 5000 entries refuses with `Rule scan is too large (over 5000 entries).`

`ref` is a template giving one entry id (the 36-character form). `field`, `state`, `related`,
`sum`, `member`, `covers` and a `field` let then read that entry instead of scanning. An id that is
not valid, an entry that is missing, one of another `contentType`, one outside `state`, or one that
belongs to a branch other than the request's counts as no entry.

`stored`: the stored entry's value (empty on a create).

```json
{ "kind": "stored", "field": "Status", "op": "ne", "value": "Posted", "message": "A posted entry cannot be edited." }
```

`ref`:

```json
{ "kind": "field", "ref": "{{OrderId}}", "field": "Status", "op": "eq", "value": "Open" }
```

`related`:

```json
{ "kind": "related", "contentType": "Order", "where": "Code={{OrderCode}}", "state": "Open,Active" }
```

`unique` holds when no other entry of this type has the same value in `field`. An empty value
passes. `normalise` lists steps applied to both sides: `trim`, `lower`, `digits` (keeps digits only).

```json
{ "kind": "unique", "field": "Number", "normalise": ["trim", "lower"] }
```

`count` and `sum` compare the total with `op` and `value`. `sum` adds the numbers at the path
`sum`, which can run through a list (`Lines.Amount`).

```json
{ "kind": "sum", "contentType": "Delivery", "where": "Batch={{Batch}}", "sum": "Lines.Amount", "op": "eq", "value": "30" }
```

```json
{ "kind": "count", "contentType": "Shipment", "where": "OrderId={{OrderId}}", "state": "Released,Amended", "op": "gte", "value": "1" }
```

`member` checks that every wanted value is allowed. The wanted values are the field's values
(a list is spread, empty values are dropped), or `value` when there is no `field`. With nothing
wanted it holds. The allowed set is what `of` gives from each source entry, plus the comma list
in `also`. `of` is a path, or a template per row such as `lab:{{TestCode}}`. With no `contentType`,
`where`, `state` or `ref`, the source is the entry being written.

```json
{ "kind": "member", "field": "Orders.Code", "of": "Code", "contentType": "Catalog", "also": "custom",
  "message": "Not in the catalog." }
```

`covers` is the reverse: every value `of` gives (plus `also`) must be among the field's values.

```json
{ "kind": "covers", "field": "Results[last].Values.$keys", "of": "Parameters.Code", "contentType": "Catalog",
  "where": "Code={{TestCode}}" }
```

`distinct` holds when the field's values have no repeats (ignoring case, after `normalise`).

```json
{ "kind": "distinct", "field": "Lines.Code", "normalise": ["trim"] }
```

In `each`, `{{$value}}` is the item when the items are plain values:

```json
{ "kind": "each", "field": "Codes", "each": [ { "kind": "member", "value": "{{$value}}", "contentType": "Catalog", "of": "Code",
  "message": "{{$value}} is not in the catalog." } ] }
```

## Lookups

A `lookup` condition or let calls a lookup a module registers, for values the rule language
cannot read.

```csharp
public interface IRuleLookup
{
    string Name { get; }
    Task<RuleLookupValue> ResolveAsync(IReadOnlyDictionary<string, string> args, IDocumentSession session, CancellationToken ct);
}

public sealed record RuleLookupValue(bool Found, string? Text, decimal? Number);
```

Register it as a scoped service:

```csharp
services.AddScoped<IRuleLookup, MyLookup>();
```

The rule names it in `lookup` (ignoring case) and passes `args`, whose values are templates:

```json
{ "kind": "lookup", "lookup": "stock.left", "args": { "Item": "{{Item}}" }, "op": "gte", "value": "1" }
```

The condition's `op` runs on the number, else the text. `empty` holds when nothing was found,
`notEmpty` when something was, and every other op holds only when something was found. A name that
is not registered refuses with `No rule lookup named '<name>' is registered.`
