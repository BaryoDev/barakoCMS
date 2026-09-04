# Content type blueprints

Every new site used to start from an empty schema, and the first hour was retyping the same content
types. A blueprint is a named set of content type definitions that one call creates in a tenant.

## Listing and applying

```
GET  /api/content-types/blueprints
POST /api/content-types/blueprints/{name}
```

Both are gated on `manage_content_types`, the capability that creates a content type, because that
is what applying one does. The list returns each blueprint's name, description, the type names it
creates, whether it is built in, and any errors found in its file. The apply creates every type in
the caller's tenant and returns their ids and names.

Applying is additive and all or nothing. If any type the blueprint declares already exists in the
tenant, the whole apply is refused with 409 and the refusal names the types that clash. Nothing is
replaced, ever. A partial apply would leave references pointing at a type whose fields are not the
ones the blueprint assumed, so one clash refuses the set. Types that the blueprint does not mention
are left alone, so applying `blog` to a tenant that already has a `newsletter` type works.

Types are created the way `POST /api/content-types` creates one: name normalized, document sourced,
the sourcing decision recorded against the name. A name that was decided event sourced before is
refused rather than recreated, for the reason [event-sourced-content-types.md](event-sourced-content-types.md)
gives.

The audit log records `contenttype.blueprint_applied` with the names created.

## The built-in four

Every addressable type has a `Slug` field of type `slug`, which is what makes
`/api/public/{type}/{slug}` exist, and every type is publicly deliverable. Fields that are for the
team and not the public are marked `Sensitive` (masked on the way out) or `Hidden` (removed).

| Blueprint | Types | Notes |
| :--- | :--- | :--- |
| `blog` | `post`, `category`, `author`, `page` | Post has body richtext, excerpt, cover image URL, published date, author and category references, tags. Author email is Sensitive. Page can nest under a parent page. |
| `events` | `event`, `venue`, `speaker` | Event has starts and ends, a venue reference and a `geopoint` location, so `filter[Location][near]` works on delivery. Venue contact details are Sensitive. |
| `portfolio` | `project`, `client` | Project has a client reference, a gallery array, a live URL and a testimonial. Client contact name and email are Sensitive, internal notes are Hidden. |
| `docs` | `article`, `section` | Article has a markdown body, a required section reference and an order within it. Section can nest under a parent section. |

The blueprints carry no SEO fields. Run `POST /api/content-types/{name}/seo-fields` on the types a
frontend renders as pages; see [seo-fields.md](seo-fields.md).

There is no media content type in the core, so an image is a `url` field. If a deployment models
media as content, a custom blueprint can reference it instead.

## Custom blueprints

Set `Blueprints:Path` to a directory. Every `*.json` file in it is listed alongside the built-ins and
applies the same way. The setting is unset by default, and the directory is read on every list and
apply, so a file dropped in is visible without a restart. At most 100 files are read; past that the
list says so under `problems`.

A file is one object:

```json
{
  "name": "agency",
  "description": "Case studies and the people who wrote them.",
  "contentTypes": [
    {
      "name": "case-study",
      "displayName": "Case study",
      "isPubliclyDeliverable": true,
      "fields": [
        { "name": "Title", "displayName": "Title", "type": "string", "isRequired": true },
        { "name": "Slug", "displayName": "Slug", "type": "slug", "isRequired": true },
        { "name": "Lead", "displayName": "Lead", "type": "reference", "referenceType": "consultant" }
      ]
    },
    {
      "name": "consultant",
      "displayName": "Consultant",
      "fields": [
        { "name": "Name", "displayName": "Name", "type": "string", "isRequired": true },
        { "name": "Rate", "displayName": "Day rate", "type": "money", "sensitivity": "Hidden" }
      ]
    }
  ]
}
```

The entries under `contentTypes` are the same shape the Portability import accepts, so a file can be
assembled from `GET /api/portability/export` by keeping the types and dropping the contents.

A file is validated when it is listed, with the validator the create endpoint runs, plus three rules
of its own:

- the name is lower-case letters, digits and hyphens, and a name a built-in already uses is refused;
- a reference must point at a type declared in the same blueprint, so applying on an empty tenant
  produces a schema that works;
- a property the type does not have is an error rather than ignored, so a misspelt `sensitivity`
  cannot leave a field Public that the author marked Hidden.

An invalid file still appears in the list, with its problems under `errors`, and applying it is a
400 repeating them. A file that does not parse is listed under its file name with the parse error.
