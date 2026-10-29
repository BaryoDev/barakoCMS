<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.Pages logo" />
  <h1>BarakoCMS.Pages</h1>
  <p><em>A page tree over a content type you already have: navigation, paths and breadcrumbs.</em></p>
</div>

---

The `blog` blueprint creates a `page` type with a slug, a parent page, a navigation flag and an
order. This module adds what that shape cannot say on its own: a page may not be its own ancestor,
may not be nested too deep, and a top-level page may not take a reserved slug. It also answers the
two questions a renderer needs and cannot compute from a list: the site menu, and which page lives
at `/about/team`.

## Enable it

```sh
dotnet add package BarakoCMS.Pages
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=Pages`). To name it by hand instead, put
`modules.Add(new BarakoCMS.Pages.PagesModule())` in the `AddBarakoCMS` callback. See `MODULES.md` in
the repository.

The module seeds nothing. It stores one row per tenant that sets its own page type (see
[Per tenant](#per-tenant)). Create the page type first, for example by applying the `blog` blueprint.

## Configuration

Under `Modules:Pages`. The defaults match the `blog` blueprint's `page` type.

| Key | Default | Meaning |
|---|---|---|
| `ContentType` | `page` | The content type holding the pages |
| `ParentField` | `ParentPage` | Reference field naming the parent; empty means a top-level page |
| `ShowInNavigationField` | `ShowInNavigation` | Boolean field that puts a page in the menu |
| `OrderField` | `NavigationOrder` | Integer field ordering siblings; unset sorts last |
| `TitleField` | `Title` | Field used as the title in the menu, breadcrumbs and tree |
| `MaxDepth` | `8` | Most ancestors a page may have |
| `ReservedSlugs` | none | Slugs a top-level page may not take, case-insensitive |
| `HomeSlug` | `home` | The top-level page served at `/` |
| `MaxPages` | `1000` | Most pages one request reads; navigation and the tree say `truncated` when there were more |

The slug field is the one public delivery already uses: a field of type `slug`, else one named `slug`.

### Per tenant

`Modules:Pages` is the deployment's default. A tenant can name its own type and fields, so two
tenants on one deployment can each have their own tree:

| Method and path | Purpose | Access |
|---|---|---|
| `GET /api/pages/settings` | What the tenant set, and what that comes to over `Modules:Pages` | `manage_content_types` |
| `PUT /api/pages/settings` | Replaces what the tenant set | `manage_content_types` |

The body takes `contentType`, `parentField`, `showInNavigationField`, `orderField`, `titleField`,
`maxDepth` (1 to 64), `reservedSlugs` and `homeSlug`. Each one left out, or null, takes the
deployment's value, so `{}` puts the tenant back on `Modules:Pages`. `MaxPages` stays the
operator's. A type the request names has to exist, and each field it names has to be a field of
the tree's type, so a typo cannot leave the real type with no rules. The answer is `{ "contract": 2, "tenant": {...}, "effective": {...} }`. A tenant that
never sets any reads exactly what `Modules:Pages` says, as before.

The rules on write, the three endpoints and the tree's `options` all read the tenant's values.

### Declaring the parent on the type

A reference field pointing at its own type may declare `"parent": { "maxDepth": 8 }` on its
definition. Core then refuses a loop or a chain too deep on every write to that type, with no
module or code involved: a docs site's sections or a shop's categories get a guarded tree from
the definition alone. When the page type declares its parent field this way, with a `maxDepth` no
deeper than the tree's, the module leaves the walk to core and keeps the reserved-slug rule.

## Rules on write

A create, update or rollback of the configured type is refused with 400 when:

- the parent field points at the entry itself, or closes a loop of any length;
- the page would have more than `MaxDepth` ancestors, or its parent's chain never reaches a root;
- a top-level page takes a slug in `ReservedSlugs`.

The parent walk runs inside the write's transaction under an advisory lock, so two concurrent saves
cannot close a loop between them. Two siblings with the same slug are already refused by core, which
refuses any slug another entry of the type holds.

## Endpoints

| Method and path | Purpose | Access |
|---|---|---|
| `GET /api/public/pages/navigation` | Nested, ordered menu of published pages flagged for navigation | Anonymous |
| `GET /api/public/pages/resolve?path=/about/team` | The page at a path, with breadcrumbs; 404 on a miss | Anonymous |
| `GET /api/pages/tree` | Every page the caller may read, drafts included, for the console | Signed in, content read permission |
| `GET /api/pages/block-schema` | The tenant's block schema as `{ version, blocks }`, for an editor; see `docs/blocks.md` | Signed in |
| `GET /api/pages/data-sources` | The read-only data sources modules registered for page blocks | Signed in, per source capability |

The two anonymous endpoints serve only what `GET /api/public/{type}/{slug}` would: published,
document-Public entries of a publicly deliverable type, with only Public fields. A page is left out of
the menu and does not resolve when any page above it is not served, so a published page under a
draft stays off the site. Tree fields are read from the Public fields only. Both answer 404 when the
type is not publicly deliverable, and both are cacheable for 60 seconds with `Vary: X-Tenant`.

A menu page whose parent is not in the menu sits under its nearest ancestor that is, and keeps its
real path. The home page has path `/`; `/home` does not resolve to it.

Every body carries `contract`, currently `2`. It moves only on a breaking change to these bodies. A
renderer that does not know the number should show no menu rather than stop.

```json
{ "contract": 2, "truncated": false, "items": [
  { "id": "...", "title": "About", "slug": "about", "path": "/about", "order": 1, "children": [
    { "id": "...", "title": "Team", "slug": "team", "path": "/about/team", "order": null, "children": [] } ] } ] }
```

```json
{ "contract": 2, "path": "/about/team",
  "entry": { "id": "...", "contentType": "page", "slug": "team", "data": { "Title": "Team" }, "createdAt": "...", "updatedAt": "..." },
  "breadcrumbs": [
    { "id": "...", "title": "About", "slug": "about", "path": "/about" },
    { "id": "...", "title": "Team", "slug": "team", "path": "/about/team" } ] }
```

`truncated` is true when the type holds more than `MaxPages` published pages, counted before the
navigation flag is read. Pages are read oldest first, so the newer ones, and every page under them,
may be missing from the menu even though resolve still serves them. Raise `MaxPages` when a site
sees it.

`entry` is the same shape `GET /api/public/{type}/{slug}` returns.

`boundBlocks` lists, for each block of a `blocks` field that names a `binding`, the entries that
binding selects: `{ "field": "Blocks", "index": 1, "binding": "...", "entries": [ ... ] }`, each
entry in the shape above. Only the first 10 such blocks are read, each binding returns at most 20
entries, and a binding whose type is not publicly deliverable, or whose sort or conditions name a
field that is not Public, returns none. Empty for a page with no bound blocks. See
`docs/blocks.md` in the repository.

```json
{ "contract": 2, "truncated": false,
  "options": { "contentType": "page", "parentField": "ParentPage", "showInNavigationField": "ShowInNavigation",
    "orderField": "NavigationOrder", "titleField": "Title", "maxDepth": 8, "reservedSlugs": [], "homeSlug": "home" },
  "items": [
  { "id": "...", "title": "About", "slug": "about", "path": "/about", "status": "Draft",
    "showInNavigation": true, "order": 1, "children": [] } ] }
```

`options` is the configuration in use for the tenant, so a console that moves a page writes the
parent and order fields this site's type has, not the defaults. Only the tree carries it; the two
anonymous bodies do not.

In the tree, a page whose parent the caller cannot read, or whose chain loops, is listed at the top
level with `path` null, so it can be found and fixed.

## Data sources

A module registers a named, read-only data source by adding a singleton `IBlockDataSource`
(`barakoCMS.Core.Interfaces`) with an `Id`, a `Shape` (the fields each row carries) and an optional
`Capability`. `GET /api/pages/data-sources` lists them ordered by id, and leaves out a source whose
capability the caller does not hold.

```json
{ "contract": 3, "items": [
  { "id": "entries", "capability": null,
    "shape": { "fields": [ { "name": "id", "kind": "id" }, { "name": "data", "kind": "object" } ] } } ] }
```

An id is a lowercase letter, then lowercase letters, digits, `.`, `_` or `-`, at most 64 characters.
A malformed id, a source with no fields, or two sources with one id stops the host at start, naming
the source. Core registers `entries` (published entries, as delivery serves them). A module adds
its own source by registering an `IBlockDataSource`.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

## License

[MIT](LICENSE) © BaryoDev
