# Field hints, sections and roles

A field definition can say three things about itself beyond its type, and a content type can say
where its entries live on the site. All four are optional. A type that sets none of them behaves as
it did before they existed.

```json
{
  "name": "school-event",
  "displayName": "School event",
  "routeTemplate": "/whats-on/{slug}",
  "fields": [
    { "name": "EventName", "displayName": "Event name", "type": "string", "role": "title", "section": "Details" },
    { "name": "Slug", "displayName": "Slug", "type": "slug", "section": "Details" },
    { "name": "Teaser", "displayName": "Teaser", "type": "text", "role": "summary", "section": "Details" },
    { "name": "StartsOn", "displayName": "Starts on", "type": "datetime", "role": "date", "section": "Details" },
    { "name": "Sections", "displayName": "Sections", "type": "json", "editor": "blocks", "section": "Page" }
  ]
}
```

None of them changes what an entry may hold. The field's type still decides that.

## editor

A hint naming the editor a console should open for the field. A console that reads it no longer has
to guess from the field's name, so a block list can be called `Sections` and a second menu type can
keep its links in `FooterItems`.

| Value | For a field of type | Meant for |
| --- | --- | --- |
| `blocks` | `json`, `array` | a page's list of blocks |
| `menu` | `json`, `array` | a navigation tree |
| `links` | `json`, `array` | a flat list of links |
| `image` | `url`, `string`, `file` | one image, by its URL or path, or the stored file a [file field](file-fields.md) names |

The `image` hint does not change what the field holds: a URL or a path, or a file field's id. A
small image kept inside the entry is a field of its own type, `inlineimage`, which takes no editor
hint. See [inline image fields](inline-image-fields.md).

The value is lower case and one of the list. Anything else is a 400 that names the accepted values
and does not repeat what was sent. A known value on a field type it is not for is a 400 naming the
types it is for. `null`, or leaving it out, is no hint, and a console then picks the editor the way
it did before.

## section

Free text naming the group a field sits in on a generated edit screen, such as `Branding`.

- At most 60 characters, no leading or trailing space, no line break. A blank string is refused;
  leave `section` out for a field in no section.
- Sections are compared exactly, case included, so `Branding` and `branding` are two sections.
- There is no separate order. Sections appear in the order of the first field that names each, and
  fields keep the type's own order inside one.

The API stores and returns the section and does nothing else with it.

## role

What the field is to its entry. The RSS feed and the SEO block read it, so they stop depending on a
field being called `Title`.

| Value | For a field of type | Read by |
| --- | --- | --- |
| `title` | `string`, `text` | the feed item's title, and the SEO title when `MetaTitle` is empty |
| `summary` | `string`, `text`, `markdown`, `richtext` | the feed item's description |
| `date` | `date`, `datetime` | the feed item's publication date |

- One field of a type holds a role. A type in which two fields declare the same role is a 400.
- The field holding a role is read first. When the entry holds nothing in it, the names that were
  guessed before roles existed are still tried, in the same order as before: `Title` then `Name` for
  the title, `Excerpt`, `Summary`, `Description` then `Body` for the description, `Date` then
  `PublishedAt` for the date. A type with no roles is read by those names alone, as it always was.
- The SEO title tries `MetaTitle`, then the field holding the title role, then the names listed in
  [seo-fields.md](seo-fields.md).
- A role does not make a field public. The feed and the SEO block read the entry as public delivery
  projects it, so a role on a field that is not `Public` contributes nothing.

## routeTemplate

On the content type, not on a field: the path of an entry on the site, holding `{slug}` once. The
feed and the sitemap join it to the configured site URL to build each link.

The order is the type's `routeTemplate`, then the `Feeds:Paths:{type}` setting, then `/{type}/{slug}`.
So a type with no template links exactly as it did, and the path of a new type no longer needs a
change to server configuration.

A template starts with `/`, holds `{slug}` exactly once, is at most 200 characters, and is otherwise
ASCII letters, digits, `-`, `_`, `.`, `~` and `/`, with no empty segment (`//`) and no `.` or `..`
segment. Anything else is a 400. The point of the rule is that the value names a path on the site
and nothing else: joined to the site URL, or resolved against it as a URL reference by a console or
a renderer, it stays on that host. So `@other.example/{slug}`, `//other.example/{slug}` and
`/../{slug}` are all refused. A stored value that fails the rule is ignored by the feed and the
sitemap.

Both answer with a one minute cache lifetime, so a changed template shows after that.

## Setting them

When the type is created, in `POST /api/content-types`: `routeTemplate` on the type, and `editor`,
`section` and `role` on each field.

When a field is added, in `POST /api/content-types/{name}/fields`: `editor`, `section` and `role`.

On a field that is already stored:

```text
PUT /api/content-types/{name}/fields/{field}/presentation
{ "editor": "blocks", "section": "Page", "role": null }
```

The three are set together. A member left out is `null`, and `null` clears it, so send the two you
are not changing as they stand. A role another field of the type holds is a 400 naming that field;
clear it there first. No entry is read or written.

On a type that is already stored:

```text
PUT /api/content-types/{name}/route-template
{ "routeTemplate": "/whats-on/{slug}" }
```

`null` clears it.

Both need `manage_content_types`. A change is recorded in the audit log as
`contenttype.field.presentation.changed` or `contenttype.routetemplate.changed`, and a request that
changes nothing records nothing.

## Reading the vocabulary

`GET /api/meta/describe` lists what a field may declare, to any signed-in caller:

```json
{
  "fieldEditors": [ { "name": "blocks", "fieldTypes": ["json", "array"] } ],
  "fieldRoles": [ { "name": "title", "fieldTypes": ["string", "text"] } ]
}
```

A console can read these lists instead of keeping its own copy. A value this API does not list is
one it would refuse.

A renderer reads a publicly deliverable type's own declaration anonymously, from
`GET /api/public/types/{type}/description`:

```json
{
  "name": "event",
  "routeTemplate": "/whats-on/{slug}",
  "fields": [
    { "name": "Title", "type": "string", "role": "title", "editor": null },
    { "name": "StartsAt", "type": "datetime", "role": "date", "editor": null }
  ]
}
```

Only the Public fields are listed, the ones delivery returns values for, and nothing about their
sensitivity, rules or defaults. `routeTemplate` is `null` when the type declares none or the stored
value fails the rule above. A type that is not publicly deliverable is a 404, the same as an unknown
one. The answer is cached for one minute and varies by `X-Tenant`, like the other delivery reads.

## Blueprints and bundles

A blueprint file and a Portability bundle carry all four, and both are held to the rules above.

A bundle imported over a stored type sets what it carries and keeps what it does not. A bundle
exported before these members existed carries none, so importing it leaves every hint, section,
role and route template as stored. A value in the bundle replaces the stored one. A bundle cannot
clear one, because a member left out and a member set to `null` read the same: clear it with the
two endpoints above. Two details: a stored hint is kept only on a field whose type the bundle
leaves as it is, and a stored role is not kept on a field when the bundle gives that role to
another one, so a bundle can move a role.

The built-in blueprints declare none of them.
