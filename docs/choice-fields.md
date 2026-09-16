# Choice fields

A `choice` field holds a value from a list the field declares, or a list of them. Use it wherever a
free string would let a typo become data: an entry type, a shirt size, an area of focus.

```json
{
  "name": "EntryType",
  "displayName": "Entry type",
  "type": "choice",
  "options": [
    { "value": "FUN", "label": "Fun run" },
    { "value": "COMPETE", "label": "Competitive" }
  ]
}
```

Add `"multiple": true` for a field that holds a list, such as the sizes a shirt comes in.

## What each part is for

- **value** is what an entry stores, what delivery returns and what a filter matches. It is matched
  exactly, case included, and should not change once entries hold it.
- **label** is what an editor sees. Reword it at any time; no entry changes.
- **order** is display order, and `GET /api/content-types` returns the options in it.

## Rules when the type is defined

- A choice lists at least one option and at most 200. A longer list belongs in its own content type,
  pointed at with a `reference`.
- Every option has a value, with no leading or trailing space, at most 100 characters. A label is at
  most 200.
- Two options whose values differ only in case are refused.
- `options` and `multiple` on a field of any other type are refused.

## Rules when an entry is saved

| Sent | Single choice | Multiple choice |
| --- | --- | --- |
| `"FUN"` | accepted | 400, send a list even of one |
| `"fun"` | 400, names the accepted values | 400 |
| `["S", "L"]` | 400, holds one value | accepted |
| `["S", "S"]` | 400 | 400, listed more than once |
| `[]` | 400 | accepted when optional, missing when required |

## Changing the options

```text
PUT /api/content-types/{name}/fields/{field}/options
{ "options": [ { "value": "FUN", "label": "5K fun run" }, ... ], "force": false }
```

The body is the full list, in order. Rewording a label, reordering and adding an option change no
entry. Removing an option that entries of any status still hold is a 409 naming how many; with
`"force": true` it goes ahead, and those entries keep their value and are refused on their next save
until an offered value is picked. Needs `manage_content_types`. Recorded in the audit log as
`contenttype.field.options.changed`.

Changing a field between single and multiple is not supported, because every entry's stored shape
would have to change with it.

## Delivery

The value is delivered as stored. The OpenAPI document describes a choice as a string `enum` of the
option values, or an array of them with `uniqueItems` for a multiple choice, so a renderer can map each
value to a colour or an icon. Filtering is covered in the [delivery API](delivery-api.md#filtering).
