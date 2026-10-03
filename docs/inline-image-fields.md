# Inline image fields

An `inlineimage` field keeps a small image inside the entry itself, as a base64 data URI, instead of
pointing at a stored file. It is for an icon, a logo or an avatar of a few kilobytes. For anything
else, upload the image to the Files module and keep its address or id in the entry: that is the
usual way to keep images in the database, since the Files module stores the bytes in Postgres by
default, one row per file.

## Opting in

The field type is the opt-in. Nothing else changes: a `url` or `string` field with the `image`
editor hint still holds a URL, and a data URI sent to a `url` field is still refused.

```json
{ "name": "Logo", "displayName": "Logo", "type": "inlineimage" }
```

## The value

An object with a `url` holding the data URI and an optional `alt`:

```json
{
  "Logo": {
    "url": "data:image/png;base64,iVBORw0KGgo...",
    "alt": "Acme logo"
  }
}
```

The keys are lower case and there are no others. Delivery returns the object as it was stored, so a
renderer reads `url` and `alt` and puts them on an `<img>`.

## What a write accepts

Every entry write that runs the entry validator applies this: create, update, rollback, a
transition carrying the field, a batch or spreadsheet import, a bundle import, a collection push and
a form submission. The checks run in this order, and the first that fails is a 400 that names the
field and the limits and does not repeat the value:

1. The value is the object above, `url` is text, and `alt`, when present, is text or null of at
   most 500 characters.
2. `url` is at most 87,407 characters (the longest allowed prefix plus the base64 of 64 KB). This is
   checked before anything is decoded.
3. `url` starts with exactly `data:image/png;base64,`, `data:image/jpeg;base64,`,
   `data:image/gif;base64,` or `data:image/webp;base64,`, lower case, with no other parameter. SVG
   is refused: it is XML that can carry script.
4. The rest is plain base64: the 64 characters, padding only at the end, a length that is a
   multiple of four, no whitespace.
5. The decoded image is at most 64 KB (65,536 bytes).
6. The bytes start with the signature of the declared type, the same signatures the Files module
   checks on upload. PNG bytes declared as JPEG, or HTML declared as PNG, are refused.
7. The width and height read from the image header are both above zero and their product is at
   most 4,194,304 pixels (2048 by 2048). No pixel is decoded on the server.

The request body limit (`RequestLimits:MaxBodyBytes`, 10 MB by default) sits in front of all of
this, as it does for every field.

A value the entry already holds is not checked again, so text stored before a field became an
inline image field does not stop the rest of the entry being saved. Sending a different value is
checked in full.

## Writers that refuse the field

- The `UpdateField` workflow action fails, without retrying, when its field is an inline image: its
  value is text and an inline image is an object.
- A collection sync that maps a value onto an inline image field is refused when it is saved. A sync
  saved before the field changed type skips the item, as it does a value that does not convert.

## Delivery

An entry is delivered with its inline image in the entry JSON. Every anonymous projection of an
entry goes through one function, so this holds on the list, the slug route (preview included),
search, `?include=` targets, the share link open route, the event stream, the Pages resolve route,
and the `data` of a `Webhook` workflow action. Before it goes out, a stored
value that is not an object of this shape with an allowed data URI prefix and a base64 payload is
left out of the entry, and an explicit `null` is kept. This check reads the url once and decodes
nothing. So only a `data:image/png`, `jpeg`, `gif` or `webp` base64 URI reaches a renderer's
`<img src>`, whatever reached the database.

The server never serves an inline image as a file. It only appears inside JSON responses, which
carry `X-Content-Type-Options: nosniff` and the API's `Content-Security-Policy` like every other
response.

## What it costs

- **Size on every response that carries the entry.** One inline image is at most about 87 KB of
  JSON. A delivery page holds at most 100 entries, so one inline image field adds at most about
  8.7 MB to a list page, and each further inline image field on the type adds that again. Lists keep
  the value; a projection that leaves it out of a list is not built yet.
- **History.** Content is event sourced and every saved version stores the whole entry, so an image
  saved ten times is stored ten times.
- **Everything else that carries the entry:** export bundles, the event stream, webhook payloads and
  any cache in front of the API.
- **No image variants, no CDN, no usage tracking and no reuse** across entries. Those come with the
  Files module.

## Search

An inline image field is left out of the entry's search text on every path in the core that builds
it (create, update, rollback, transitions, push, sync, the sensitivity change rebuild and the startup
backfill), so neither the base64 nor the alt text is searchable through public search.
