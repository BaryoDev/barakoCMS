# Structured data

A content type can ask delivery to describe each entry as schema.org JSON-LD, so a search engine
or a link preview reads a headline, a date and an image instead of a generic page. The block is
built from the type's [field roles](field-hints-and-roles.md#role), never from field names, and a
type that declares nothing emits nothing.

## Declaring it

`structuredDataType` on the content type, one of these, spelled exactly:

| Value | Title role fills | Date role fills | Also |
| --- | --- | --- | --- |
| `Article`, `NewsArticle`, `BlogPosting` | `headline` | `datePublished` | `dateModified`, `author` |
| `WebPage` | `name` | `datePublished` | `dateModified` |
| `Event` | `name` | `startDate` | |
| `Product` | `name` | (not used) | |

Set it when the type is created, in `POST /api/content-types`, or on a stored type:

```text
PUT /api/content-types/{name}/structured-data
{ "structuredDataType": "NewsArticle" }
```

`null` clears it. Any other value is a 400 that lists the accepted ones. The route needs
`manage_content_types`, and a change is recorded in the audit log as
`contenttype.structureddata.changed`. `GET /api/meta/describe` lists the values under
`structuredDataTypes`, and `GET /api/public/types/{type}/description` reports the type's own.

## What a read returns

`GET /api/public/{type}/{slug}` carries the block as `structuredData`:

```json
{
  "slug": "flood",
  "data": { "Headline": "Flood waters recede", "Teaser": "The river is back inside its banks." },
  "structuredData": {
    "@context": "https://schema.org",
    "@type": "NewsArticle",
    "headline": "Flood waters recede",
    "description": "The river is back inside its banks.",
    "datePublished": "2026-10-07T06:00:00Z",
    "dateModified": "2026-10-08T09:30:00Z",
    "image": "https://cdn.example.com/flood.jpg",
    "author": { "@type": "Person", "name": "Ana Reyes" }
  }
}
```

| Property | From |
| --- | --- |
| `headline` or `name` | the field with the `title` role |
| `description` | the field with the `summary` role |
| `datePublished` or `startDate` | the field with the `date` role, as stored, when it reads as a date |
| `dateModified` | the entry's `updatedAt` |
| `image` | the field with the `image` role: a `url` field's address, or a `file` field's public URL |
| `author` | the field with the `author` role, as a `Person` |
| `url` | the SEO `CanonicalUrl`, when the type has [SEO fields](seo-fields.md) and it is set |

A property whose field is empty is left out. An entry with no title has no block at all, since
every type above needs a name or a headline. The key is absent, not `null`, whenever there is no
block. The list route, search and the event stream do not carry it.

## What it never holds

The block is read off the response delivery is about to send, after the field allowlist, file
resolution and the reference filter. So a field that is not `Public` is not in it, a file that is
not public gives no image, and an `image` that is not an `http` or `https` address is left out.
There are no masking rules of its own to drift from delivery's.

## Embedding it

The block is a JSON object, and the serializer escapes every value, so it is always valid JSON.
Putting it in a page is the renderer's job: serialize it into
`<script type="application/ld+json">` and escape `<` (as `<`) so a value holding
`</script>` cannot close the element.
