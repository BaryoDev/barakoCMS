# JSON field schemas

A `json` field holds an object or an array. With no `jsonSchema` it accepts any, which is what
every json field stored before 5.0 does. With one, every write to the field has to match it.

```json
{
  "name": "HeaderLinks",
  "type": "json",
  "jsonSchema": {
    "type": "array",
    "items": {
      "type": "object",
      "required": ["label", "href"],
      "properties": {
        "label": { "type": "string" },
        "href": { "type": "string", "pattern": "^(/(?!/)|https?://)" }
      }
    }
  }
}
```

A write that does not match is a 400 naming the path that failed, from the field's name:

```
Field 'Header links' (HeaderLinks) does not match its schema: HeaderLinks[0] should be an object and is a string.
```

At most five failures are named per field.

## The keywords

A subset of JSON Schema 2020-12:

| Keyword | Checks |
| :--- | :--- |
| `type` | `object`, `array`, `string`, `number`, `integer`, `boolean` or `null`, or a list of them |
| `properties` | A schema per named key of an object |
| `required` | Keys an object has to hold |
| `additionalProperties` | A schema for the keys `properties` does not name, or `false` to refuse them |
| `items` | A schema every item of an array has to match |
| `enum`, `const` | The value is one of a list, or exactly one value |
| `minLength`, `maxLength`, `pattern` | A string's length in characters, and a regular expression it has to match somewhere |
| `minimum`, `maximum` | A number's bounds, inclusive |
| `minItems`, `maxItems` | An array's length |

`title`, `description`, `default`, `examples`, `format`, `$comment` and `$schema` are accepted and
check nothing, `format` included, as 2020-12 makes it by default. `GET /api/meta/describe` lists
every keyword under `jsonSchemaKeywords`.

Any other keyword, `$ref`, `oneOf` and `anyOf` among them, is refused when the type is saved. A
schema the API would read as saying less than it does would pass the values it was written to
refuse, so it is not stored at all. A schema nests at most 16 levels and is at most 64 KiB, and a
pattern that takes longer than 100 ms on a value counts as not matching.

## Stored values

A value an entry already holds is not checked again when an edit leaves it as it is, so a schema
added to a field with entries does not block editing their other fields, and a stored value that
fails it keeps reading. The next write that changes the value has to match.

## Undeclared fields

From 5.0.0 a write naming a field its type does not declare is a 400 naming it, for every type
with a definition. Names are matched ignoring case. A key an entry already holds with the same
value is let through, so an entry stored before this, or before a field was removed from its
type, can still be edited. A type with no definition takes any key, as before.
