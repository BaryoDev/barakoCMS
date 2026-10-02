# File fields

A `file` field holds one stored file. The entry keeps the file's id, and a read answers the file
it names: where to fetch it, its name, type and size, and the alt text and caption an editor wrote
on the file. Alt text lives on the file, so it is written once and every entry naming the file
reads it.

Files are stored by the Files module (`BarakoCMS.Files`), through `IFileStore`. The field type is
in the core and needs no reference to that module.

```json
{ "name": "Cover", "displayName": "Cover", "type": "file", "editor": "image" }
```

A file field takes no `referenceType`, `options`, `currency` or text rule (`minLength`,
`maxLength`, `pattern`). It may take `editor: "image"`, which tells a console to open an image
picker rather than a picker for any file. It may take `requiredWhen`, `section` and `sensitivity`
like any other field.

## What an entry stores

The id as text, written with hyphens, 36 characters:

```json
{ "Title": "Harbour", "Cover": "6f9619ff-8b86-d011-b42d-00cf4fc964ff" }
```

Nothing else about the file is copied into the entry. An id written any other way (no hyphens,
braces, a download URL) is refused, because the Files module finds the entries that use a file by
looking for the id as written there.

Send `null`, or leave the field out, for no file.

## Who may attach a file

A write may name a file the caller could download:

- any public file of the tenant;
- a private file the caller uploaded, or any private file when the caller holds Admin or
  SuperAdmin.

Everything else is refused with one message, `Field 'Cover' takes the id of a stored file you may
use, and this value is not one.`, whatever the reason: text that is not an id, an id no file has, a
file that was deleted, a file of another tenant, a cached resize, and a private file of somebody
else. The value is not repeated in the message, so a write cannot be used to learn which ids name
a file. An API key reads public files only, so it may attach a public file only.

The value the entry already holds in the field is not checked again. A colleague who may edit the
entry but not download its file can still save the entry, and an entry whose file was deleted can
still be edited. Changing the field to another file is a new attachment and is checked.

A write that sends the field under two keys differing only in case, such as `Cover` and `cover`,
is refused: only one of them would be checked, and a read answers both.

| Writer | Caller asked about | What it may attach |
| --- | --- | --- |
| `POST /api/contents`, `PUT /api/contents/{id}`, a batch, a rollback, a collection push, a spreadsheet import, a bundle import | the signed-in user of the request | a public file, or a private one that user may download |
| A transition carrying the field (`IContentTransitioner`) | the actor the move is made for, not the user whose request is running | for a user actor, a public file or one that user uploaded; for a system actor, a public file only |
| A module calling `IContentValidatorService` itself | the `caller` it passes; none passed means no user | with no caller, a public file only |
| A form submission (`BarakoCMS.Forms`) | nobody: a form does not offer a file field | nothing |
| The `UpdateField` workflow action | nobody: a workflow runs for no user | a public file only; anything else fails the action for good |
| A collection sync | nobody | nothing: a sync that maps a source value onto a file field is refused when it is saved |

A bundle carries entries and not files. Importing one where the files it names are missing (into
another deployment, or a tenant rebuilt from nothing) refuses every entry naming a missing file,
and an import commits all of its entries or none, so the whole import is refused. Restore the files
first, with their ids, then import the bundle.

## What a read answers

### Anonymous delivery

`GET /api/public/{type}`, `GET /api/public/{type}/{slug}` (with or without a preview token),
`GET /api/public/{type}/search`, an entry resolved through `?include=`, and the entry an entry or
page share link opens replace the id with the file, for a public file:

```json
"Cover": {
  "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
  "url": "/api/public/files/6f9619ff-8b86-d011-b42d-00cf4fc964ff",
  "fileName": "harbour.png",
  "contentType": "image/png",
  "size": 48213,
  "alt": "Boats at dawn",
  "caption": null
}
```

`url` is an absolute URL when an object store serves the file itself, and otherwise a path to join
to the address the API is served from. `alt` and `caption` are null until an editor writes them
(`PATCH /api/files/{id}`). These are the same members `GET /api/public/files/{id}/meta` answers
anonymously, so nothing here was private before.

Every other file is left out of the entry: the field is absent, not `null` and not the id. That
covers a private file, a file that was deleted, an id that is not a file, text stored before the
field was a file field, and every file on a host with no module that stores files. The entry
itself is still delivered. This is the rule the Files module's anonymous routes apply, and the
rule a workflow's email attachments follow: work done for no signed-in caller reads public files
and nothing else.

A file field the type does not mark Public is not delivered at all, as for any field. When the
SEO field `SocialImage` is a file field, the SEO block's `imageUrl` is that file's `url`, or null.
The delivery OpenAPI document describes a file field as this object and never lists it as
required, since it can be absent from any entry.

The Pages module's `GET /api/public/pages/resolve` answers a page's file fields the same way,
through `IPublicContentProjector.ProjectAsync`. A module serving entries of its own does the same.

Three payloads leave every file field out, public files included, because they do not read the
file store: the server-sent event stream (`/api/public/events`, built inside the commit),
webhook payloads (a workflow has no caller to read as), and `IPublicContentProjector.Project`, the
projector's synchronous member. A `SocialImage` file field gives them no SEO image.

The id itself is not secret: it names nothing an anonymous reader can fetch, since the public
download and metadata routes refuse a private file. It is still in the entry's search text when the
field is Public, so `search?q=` or `filter[Cover][eq]=` with a known id answers which published
entries hold it, without answering the field.

### Authoring reads

`GET /api/contents/{id}`, `GET /api/contents/by-slug/{type}/{slug}` and `GET /api/contents` keep
the id in `data`, because `data` is what a client sends back on its next save. Beside it, a
`files` member holds the file each field names, keyed as the field is in `data`, for the files the
caller may download (the rule above for writes):

```json
"data": { "Title": "Harbour", "Cover": "6f9619ff-8b86-d011-b42d-00cf4fc964ff" },
"files": {
  "Cover": { "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff", "url": null, "fileName": "harbour.png", "contentType": "image/png", "size": 48213, "alt": "Boats at dawn", "caption": null }
}
```

`url` is null for a private file: fetch its bytes from `GET /api/files/{id}` with the caller's
token. `files` is left out when no field resolves. The sensitivity scrub runs first, so a field
masked from the caller names no file in `files` either.

## Cost and bounds

A response reads the file store once for every 500 distinct file ids it names, whatever the number
of entries and fields. A page is at most 100 entries, so a page with five file fields on every
entry is one read. Past 500 ids nothing is dropped: the rest are read in further batches of 500.
An `?include=` adds one more read for the included entries' files. An authoring read adds one query
for the content types on the page.

## Deleting a file

An entry naming a file in a file field uses it. `DELETE /api/files/{id}` answers 409 with the
entries using it, and `GET /api/files/{id}/usage` lists them, as for a file named in any other
field. `?force=true` deletes anyway. The entries keep the id: delivery leaves the field out, the
authoring read keeps the id in `data` with no `files` entry for it, and the entry can still be
edited.

## Without the Files module

A host with no module that stores files still starts, and a type may still declare a file field.
An entry write naming a new file is refused with `Field 'Cover' holds a stored file, and no module
that stores files is enabled. Enable BarakoCMS.Files and restart.` An entry written while the
module was enabled can still be edited, and delivery leaves its file fields out.

## Existing fields

A `string` or `url` field is not converted. One that holds a `/api/public/files/{id}` URL keeps it,
the `image` editor hint on it keeps working, and the Files module still counts it as a use of the
file. A field whose type is changed to `file` by a bundle import keeps what its entries hold; such
text does not resolve, and a save that sends it back unchanged is accepted.

Image width and height are not part of the answer: the Files module does not record them yet.
