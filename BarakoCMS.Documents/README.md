# BarakoCMS.Documents

Renders an entry to a PDF and stores it through the Files module. The PDF is drawn with SkiaSharp.
There is no browser, and the template language has no way to fetch anything.

```sh
dotnet add package BarakoCMS.Documents
```

Enable the Files module as well. A workflow then renders an entry with:

```json
{ "Type": "RenderDocument", "Parameters": { "Template": "invoice" } }
```

The stored file's id is the action's `FileId` output. The file is private.

## Template language

One line is one block.

```
@page Letter          optional, first line only. A4 is the default.
# Invoice {{Number}}  title
## Bill to            heading
---                   rule
| Item | Qty | Price |   row, equal columns, up to 8
Thank you, {{Customer}}.  paragraph, wrapped to the page
```

A line starting with `@` that is not `@page` is refused. There is no image, link, font or include syntax.

## What a template may name

A document is an egress path, so a template naming a field that is Sensitive, Hidden or Members is
refused, the same as an outbound request. The check runs on every render, not only when the template
was saved. A hole that names no field of the content type is refused too.

## Idempotency

Two runs with the same idempotency key store one document. The second returns the first's file id.
