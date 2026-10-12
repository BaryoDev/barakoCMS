# Blocks

A `blocks` field holds a page's blocks: a list of objects, each naming its `type`, with the
block's properties beside it.

```json
[
  { "type": "hero", "id": "b1", "heading": "Welcome", "tone": "accent" },
  { "type": "cards", "binding": "6f1c...", "heading": "Latest news" }
]
```

`type`, `id`, `preset` and `binding` belong to the block itself. Every other key is a property of
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
`preset` or `binding`.

The renderer route uses the deployment's renderer key (`RateLimiting:Renderer:Key`, the same
secret that gives the renderer its own rate limit bucket), names the tenant the way a delivery
request does, and answers 404 when no key is configured and 401 for a missing or wrong key. Whoever
holds the key can replace any tenant's block schema; treat it as the secret it is.

With a schema registered, a write to a `blocks` field is a 400 naming the path when a block's type
is not in the schema, a block carries a property its block does not declare
(`Blocks[2].colour is not a property of 'hero'`), a property fails its schema, or `preset` or
`binding` is not an id. With none registered, only the shape is checked, so a page type moved to
`blocks` keeps saving until its renderer registers. A list the entry already holds is not checked
again when an edit leaves it as it is. A field holds at most 200 blocks.

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
