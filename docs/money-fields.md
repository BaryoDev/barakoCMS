# Money fields

A `money` field holds an amount. On its own it is a plain number, the same as `decimal`. Declare a
currency on it and every amount in the field is in that currency, held to that currency's decimal
places.

```json
{ "name": "Total", "displayName": "Total", "type": "money", "currency": "USD" }
```

## What the field declares

- **currency** is an ISO 4217 code, three capital letters. `usd` is refused, not corrected.
- **scale** is the most decimal places an amount may carry, from 0 to 8. Leave it out and it is the
  currency's own: 2 for USD, 0 for JPY, 3 for KWD. Set it to hold a unit price at 4 places, or to use
  a code the built-in list does not have. The list is compiled in and nothing is looked up over the
  network, so a code newer than the release needs its `scale` declared.
- A non-negative amount is the existing `min` rule: `"validationRules": { "min": 0 }`.
- `currency` and `scale` on a field of any other type are refused, and so is a `scale` with no
  `currency`.

A money field with no `currency` behaves exactly as it did before currencies existed. Nothing below
applies to it.

## The wire shape does not change

An entry stores, accepts and returns the amount as a plain JSON number, with or without a currency:

```json
{ "Total": 1250.50 }
```

The currency is on the field definition, from `GET /api/content-types`, and not in the entry. So a
filter (`filter[Total][gte]=100`), a sort, an export and an import read the same value they always
did, and declaring a currency rewrites no entry.

One currency per field. An entry cannot carry its own code, and nothing converts between codes.

## The rounding rule

There is none, on purpose: the API never rounds an amount, on write or on read.

| Field | Sent | Result |
| --- | --- | --- |
| USD | `12.5`, `12.50`, `12` | accepted |
| USD | `12.500` | accepted, the extra zero is not a decimal place |
| USD | `12.345` | 400 naming the field, the currency and the amount |
| JPY | `100.5` | 400 |
| KWD | `1.234` | accepted |
| USD | `"12.50"` (text) | 400, send a JSON number |
| no currency | any of the above | accepted, as before |

Amounts are read as decimals and compared in PostgreSQL as `numeric`, never as binary floating
point. A decimal holds 28 significant digits. A number longer than that is outside what this rule
covers: the JSON reader has already shortened it, or handed it over as floating point, which a field
with a currency refuses.

Every path that writes an entry through the entry validator applies this: create, update, a status
change that carries fields, a version restore, bulk import, a bundle import, a collection push and a
form submission. A collection sync applies it too and skips an item that does not fit. The
`UpdateField` workflow action validates nothing it writes, on any field type, and that is unchanged.

## Declaring a currency on a field that already has entries

```text
PUT /api/content-types/{name}/fields/{field}/currency
{ "currency": "USD", "scale": null, "force": false }
```

No entry is rewritten. An amount that fits is now read in the declared currency.

- Entries of any status holding an amount with more decimal places than the scale, or a value that
  is not a number, are counted. If there are any the answer is a 409 naming how many. With
  `"force": true` it goes ahead: those entries keep their value, and each is refused on its next
  save, or when a version holding it is restored, until the amount is corrected.
- Changing from one code to another on a field that entries hold amounts in is a 409 the same way.
  Nothing is converted, so with `force` every stored amount is read under the new code.
- `"currency": null` clears it and the field is a plain number again.

Needs `manage_content_types`. Recorded in the audit log as `contenttype.field.currency.changed`.

A bundle import does not change the currency or scale a stored field declares. A bundle that
carries a different one, or none, is refused and points here.
