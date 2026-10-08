# Int fields

A field of type `int` (also accepted as `integer` or `number`) holds a whole number from
-9223372036854775808 to 9223372036854775807, the range of a 64-bit signed integer (Int64). The
name says int, and it means Int64. That was decided in #706, because the API already stored values
past Int32 and refusing them would have broken writes that work today.

Every input path takes the same range:

- A request body, which is how `POST /api/contents`, updates, forms and imports send a number.
  The body is read with every whole number that fits Int64 as one, so this is the path an HTTP
  write takes.
- Numeric text, such as a spreadsheet cell, `"3000000000"`.
- A raw JSON number, which is how a module or a stored value can arrive.

A fraction (`1.5`, `1.0`) and a number past Int64 are refused with a 400 that names the field.

Delivery filters and sorts an int field as a number, at any size in the range:
`filter[Views][gt]=2147483647` and `sort=-Views` work on values past Int32. `min` and `max` rules
compare at the same range. A value stored before this, which is always inside Int32, reads as it
did. The OpenAPI document describes the field as `integer`, format `int64`.

A JavaScript client reads numbers as doubles, which are exact only up to 9007199254740991
(2^53 - 1). A value past that needs a client that reads JSON numbers as big integers.
