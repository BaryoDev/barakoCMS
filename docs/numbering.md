# Numbering

A content type can number its entries: an invoice `INV-2026-00042`, a booking `BK-00317`, a
student number. The rule says which field takes the number and how numbers are handed out.

```
PUT /api/content-types/{name}/numbering
{ "field": "Number", "prefix": "INV-", "padding": 5, "reset": "yearly", "timeZoneId": "Asia/Manila", "strategy": "gapless" }
```

It needs `manage_content_types`. `{ "field": null }` takes the rule off. The answer carries the rule
and an `example` of the next number's shape.

- `field`: a `string` or `text` field of the type that is not required. The server fills it when
  an entry is created, and replaces whatever the request sent there.
- `prefix`: up to 20 characters, put in front.
- `padding`: minimum digits, 1 to 18. A longer number is never cut.
- `reset`: `never`, `yearly`, `monthly` or `daily`. The period is written between the prefix and the
  digits, `INV-2026-00042`, and the count starts again at 1 each period.
- `timeZoneId`: the IANA zone whose midnight starts a period. UTC when left out.

Each type counts in its own series, per tenant, so two tenants never share a count or see each
other's numbers. Entries are numbered when they are created; an entry that already exists keeps
what it holds. A dry run takes no number. An update, by any route, keeps the stored number: a
number sent with it is replaced by the one the entry holds.

Entries are numbered when they are created through `POST /api/contents`, the batch create and an
import. Four other routes create entries without a number today: a collection push, a form
submission, a workflow's create-task action and an inbound collection sync. A type whose numbering
matters should not take entries by those routes until they number too.

A numbering rule moves with its field when the field is renamed, and a field a rule numbers cannot
be converted or removed until the rule is taken off. That holds for `POST /api/config/apply` and a
Portability import too: a definition leaving out the field the stored rule numbers is refused.

## Strategies

The choice is made here, where its cost can be seen:

| `strategy` | Duplicates | Gaps | Order | Cost |
| :--- | :--- | :--- | :--- | :--- |
| `gapless` (default) | never | never | kept | The number is taken inside the entry's own transaction and the series stays locked until it commits, so writers on one series take turns. A write that fails gives its number back. For an invoice, where a gap has to be explained |
| `sequence` | never | when a write fails after taking its number | kept | The number is taken and committed on its own; writers wait only for that one statement |
| `hiLo` | never | when an instance restarts with numbers left in its block | not kept between instances | Each instance takes 50 numbers at once and hands them out from memory. For a high write rate where a number only has to be unique |

Changing the strategy keeps the series' count.

## For a module

The same primitive is `INumberStrategies.For(strategy)`, which returns an `INumberAllocator`. Its
`NextAsync` overload taking a date counts in that date's period, for a number that belongs to
another day than today. The Accounting module numbers journal entries this way: series
`accounting.je`, gapless, `JE-<year of the entry's date>-<six digits>`, the format it always used.
`migrations/5.0.0/accounting-number-sequences.sql` carries each tenant's yearly count over from
the module's own `NumberSequence` rows, so numbering continues where it stopped.
