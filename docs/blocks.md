# Blocks

A `blocks` field holds a page's blocks: a list of objects, each naming its `type`, with the
block's properties beside it.

```json
[
  { "type": "hero", "id": "b1", "heading": "Welcome", "tone": "accent" },
  { "type": "cards", "binding": "6f1c...", "heading": "Latest news" }
]
```

`type`, `id`, `preset`, `binding` and `dataSource` belong to the block itself. Every other key is a property of
the block.

## The block schema

Which blocks a tenant's pages may hold is the tenant's block schema, which its renderer registers:

| Method and path | Who | Does |
| :--- | :--- | :--- |
| `PUT /api/renderer/blocks/schema` | the renderer, with `X-Barako-Renderer-Key` | Replaces the schema |
| `PUT /api/blocks/schema` | `manage_content_types` | Replaces the schema |
| `GET /api/blocks/schema` | any signed-in caller | The schema, or 404 while none is registered |

```json
{ "blocks": {
  "hero": { "type": "object", "required": ["heading"], "properties": {
    "heading": { "type": "string" }, "tone": { "enum": ["page", "accent"] } } },
  "cards": { "type": "object", "properties": { "heading": { "type": "string" } } } } }
```

Each block is described by a schema in the subset a json field's `jsonSchema` takes
([json-field-schemas.md](json-field-schemas.md)). Block names start with a letter and hold letters,
digits, `-` and `_`. At most 200 blocks, 256 KiB in all, and no block may declare `type`, `id`,
`preset`, `binding` or `dataSource`.

The renderer route uses the deployment's renderer key (`RateLimiting:Renderer:Key`, the same
secret that gives the renderer its own rate limit bucket), names the tenant the way a delivery
request does, takes a body of at most 512 KiB, judges it only after the key matches, and answers 404 when no key is configured and 401 for a missing or wrong key. Whoever
holds the key can replace any tenant's block schema; treat it as the secret it is.

With a schema registered, a write to a `blocks` field is a 400 naming the path when a block's type
is not in the schema, a block carries a property its block does not declare (unless the block's
schema says what its other properties may be with `additionalProperties`)
(`Blocks[2].colour is not a property of 'hero'`), a property fails its schema, or `preset` or
`binding` is not an id. With none registered, only the shape and the `preset`, `binding` and
`dataSource` keys are checked, so a page type moved to
`blocks` keeps saving until its renderer registers. A list the entry already holds is not checked
again when an edit leaves it as it is. A field holds at most 200 blocks.

## Data sources and the served schema

A block may name a data source in `dataSource`: the id of a source a module registered, such as
core's `entries`. A module adds its own source through `IBlockDataSource`. `GET /api/pages/data-sources` lists them. A write naming an id no module
registered is a 400, `Blocks[1].dataSource 'nope' is not a registered data source`, with or without
a block schema. `binding` keeps its meaning: it is still the id of a saved binding.

`GET /api/pages/block-schema` serves the stored schema in the shape barakoPress publishes, to any
signed-in caller. A tenant with no schema gets `{ "version": 1, "blocks": [] }`.

```json
{ "version": 1, "blocks": [ { "type": "hero", "label": "Hero", "perViewer": false, "fields": [
  { "name": "heading", "kind": "text", "label": "Heading", "required": true },
  { "name": "tone", "kind": "select", "label": "Tone", "required": false, "options": ["page", "accent"] } ] } ] }
```

The stored schema is JSON Schema, so the label, kind and per-viewer flag come from optional hints, or
from defaults when a hint is absent:

| Served | Hint | Default |
| :--- | :--- | :--- |
| block `label` | `x-label` on the block | the block name, split on `-`, `_` and case changes: `hero-banner` is `Hero banner` |
| `perViewer` | `x-perViewer` on the block (`true` or `false`) | `false` |
| field `label` | `x-label` on the property | the property name, the same way |
| field `kind` | `x-kind` on the property: `text`, `markdown`, `url`, `number`, `boolean`, `select` or `slots` | `select` for a string `enum`, `url` for a string with format `uri`, `url` or `uri-reference`, `text` for another string, `number` for `number` or `integer`, `boolean`, `slots` for an array of arrays, else `text` |
| field order | `x-order` on the property, a whole number, lower first | after every field that has one, by name |
| field `options` | none | the values of a string `enum`, for a `select` |
| field `min`, `max` | none | `minimum` and `maximum` of a number, `minItems` and `maxItems` of slots |
| field `required` | none | the name is in the block's `required` list |

Hints are checked when the schema is registered (a label is 1 to 100 characters) and never change
whether a block is valid. Blocks are served ordered by name. Fields are served by `x-order` ascending, then fields without one, ties by name,
since the stored schema keeps no declaration order. `x-order` is not served. A non-integer `x-order` is refused when the
schema is registered, and counts as none if one is stored anyway.

## Presets and bindings

Both are tenant data, listed a page at a time to any signed-in caller, and written by a caller
holding `manage_content_types`.

| Method and path | Does |
| :--- | :--- |
| `GET /api/blocks/presets` | Saved blocks, by name |
| `PUT /api/blocks/presets/{id}` | Creates or replaces one: `{ "name", "blockType", "props" }` |
| `DELETE /api/blocks/presets/{id}` | Removes one |
| `GET /api/blocks/bindings` | Data bindings, by name |
| `PUT /api/blocks/bindings/{id}` | Creates or replaces one: `{ "name", "contentType", "sort", "limit", "where" }` |
| `DELETE /api/blocks/bindings/{id}` | Removes one |

A preset's `props` are checked against its block's schema, as a page block is. A binding names a
content type that exists, an optional `sort` field (`-` first for descending), a `limit` of 1 to
20, and up to five `where` conditions, each a field the entries have to hold exactly, ignoring
case. The sort and condition fields have to be fields of the type. A tenant holds at most 500
presets and 200 bindings.

Renaming a field through `POST /api/content-types/{name}/fields/{field}/change` renames it in every
binding that sorts or filters on it. Removing a field a binding names is refused with 409 until the
binding stops naming it, and so is saving a binding on a type while a field change runs on it. See
[field changes](field-changes.md#block-bindings).

## Resolve

`GET /api/public/pages/resolve` returns, beside the page, `boundBlocks`: for each block that names
a `binding`, the published entries the binding selects, delivered as anonymous delivery would
deliver them (Public fields only). A binding whose type is not publicly deliverable, or whose sort
or conditions name a field that is not Public, selects nothing. At most 10 bound blocks per page
are read.
