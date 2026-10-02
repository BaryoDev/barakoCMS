# Site settings

A site's identity, theme and chrome are one entry of a singleton content type in its tenant, not a
file in the site's repository. barakoPress reads that entry per request, so one renderer can serve
several sites and a change in barakoBrew reaches the site on the next publish. This is the shape
decided for #793, as part of the configured sites plan (#722).

## Creating it

```
POST /api/content-types/blueprints/site
```

Creates the `site` type in the caller's tenant: publicly deliverable, and a singleton, so the tenant
holds exactly one entry. Then create that entry once and edit it from then on. Applying the
blueprint in a tenant that already has a `site` type is a 409, like any blueprint.

A renderer reads it anonymously:

```
GET /api/public/site
```

The list holds the one published entry. A draft is not delivered, so publishing is what makes a
theme change live.

## Fields

Plain fields hold identity. JSON fields hold the parts a renderer reads as structures. The API checks
that a JSON field holds valid JSON and nothing more; the barakoBrew Site and Theme screens check the
shapes below, and barakoPress falls back to its defaults for anything missing or unreadable, so a
half-filled theme renders rather than breaks.

| Field | Type | What |
| :--- | :--- | :--- |
| `Name` | string, required | The site's name, used in the header, titles and feeds |
| `Tagline` | string | One line under the name |
| `Url` | url | The site's canonical origin, from which absolute links are built |
| `Locale` | string | For dates and `lang`, for example `en-PH` |
| `Currency` | string | The three-letter code the `money` binding format formats against. Unset, a bound amount renders as a plain number |
| `Logo`, `FooterLogo`, `Favicon`, `ShareImage` | url | Uploaded files or any absolute URL |
| `LogoAlt` | string | Alt text for the logo |
| `Colors` | json | Named colours |
| `Fonts` | json | Font families by role |
| `Radii` | json | Corner radii |
| `Layout` | json | Content widths |
| `Space` | json | The spacing scale the tenant overrides |
| `Text` | json | The type scale the tenant overrides |
| `Collections` | json | Content types rendered as lists and detail pages |
| `OptionColors` | json | A colour per option of a choice field |
| `Variants` | json | Themes a visitor can switch between |
| `TopBar` | json | The strip above the header |
| `HeaderLinks` | json | Links in the header beyond the page tree |
| `FooterColumns` | json | Footer link columns |
| `SocialLinks` | json | Social profiles |
| `Copyright` | string | The footer's copyright line |
| `Mode` | string | `Live` or `Holding`. Unset means `Live`. See [Holding a site back](#holding-a-site-back) |
| `HoldingPath` | string | The site path of the page shown while holding, such as `/holding` |
| `HeaderPath`, `FooterPath` | string | The site path of a page drawn as the header or footer region in place of the built-in one |
| `HeaderTone`, `FooterTone` | choice | The tone behind that region: `page`, `surface`, `accent`, `inverse`, `gradient` or `wash` |
| `Tokens` | json | Named colours, lengths and font stacks. See [Tokens and Tones](#tokens-and-tones) |
| `Tones` | json | Named tones built from tokens. See [Tokens and Tones](#tokens-and-tones) |
| `StyleRecipes` | json | Named looks a block wears with `recipe`. See [StyleRecipes](#stylerecipes) |
| `MenuLinks` | json | The phone menu's rows. See [MenuLinks and HeaderActions](#menulinks-and-headeractions) |
| `HeaderActions` | json | Call to action links after the header links. See [MenuLinks and HeaderActions](#menulinks-and-headeractions) |
| `Plugins` | json | The plugins this tenant renders. See [Plugins](#plugins) |
| `Presets` | json | Saved blocks a designer builds in barakoBrew, which barakoPress renders |
| `HomePath` | string | The site path of the page served at `/`, such as `/home`. Unset, `/` is what the renderer serves by default |
| `Labels` | json | The words the renderer's screens print for a visitor. See [Labels](#labels) |
| `OptionStyles` | json | A tone, icon and word per option of a choice field. See [OptionStyles](#optionstyles) |

### Colors

An object of colour name to hex. The names barakoPress reads today are its theme slots (`pageBg`,
`surface`, `ink`, `proseInk`, `secondaryInk`, `muted`, `hairline`, `accent`, `accentHover`,
`accentInk`, `accentTint`, `accentTintBorder`, `accentTintBorderStrong`, `darkPanel`,
`darkPanelChrome`, `darkPanelInk`, `darkPanelAccent`, `codeGreen`, `success`). Any other name is a
site colour blocks can refer to.

```json
{ "accent": "#17458F", "ink": "#1C1C1C", "pageBg": "#FFFFFF", "royalBlue": "#17458F", "gold": "#F7A81B", "cranberry": "#D41367" }
```

### Fonts, Radii, Layout

```json
{ "heading": "Zilla Slab", "body": "Open Sans", "mono": "JetBrains Mono" }
```

Family names as Google Fonts spells them. The renderer loads them and adds a fallback stack.

```json
{ "panel": "2px", "control": "2px", "pill": "999px" }
```

```json
{ "prose": "680px", "wide": "1200px", "gutter": "32px" }
```

CSS lengths.

### Collections

Keyed by a name of the tenant's choosing, each entry a content type rendered as a list and a detail
page: a hospital's doctors, a law firm's people, an association's events. This is the shape
barakoPress reads (BaryoDev/barakoPress#5); a key that does not read as one below is left out whole
rather than half applied.

```json
{
  "events": {
    "type": "event",
    "route": "/events",
    "fields": {
      "title": "Title",
      "slug": "Slug",
      "summary": "Description",
      "date": "StartDate",
      "image": "CoverImage",
      "tags": "Tags"
    },
    "sort": "-StartDate",
    "colorBy": "EntryType",
    "label": "Upcoming events",
    "noun": ["event", "events"]
  }
}
```

| Key | What |
| :--- | :--- |
| `type` | Required. The content type holding the items |
| `route` | The index is served here, an item at `{route}/{slug}`. Absent, items are listed but never linked |
| `fields.title` | Required. A field name, or several tried in order until one holds a value |
| `fields.slug`, `summary`, `body`, `date`, `image`, `imageAlt`, `featured`, `tags`, `url`, `photo`, `progress` | Optional field roles, each the same shape as `title` |
| `references` | Reference fields by field name, for example `{ "Speaker": { "collection": "person", "label": "with" } }` |
| `sort` | Sent to the API as is, for example `-StartDate` |
| `feed`, `sitemap`, `index` | Booleans. `feed` is off, `sitemap` and `index` are on, unless said otherwise |
| `pageSize` | Items on its index, 1 to 100 |
| `label`, `noun` | The index heading, and a singular and plural for a count, for example `["event", "events"]` |
| `colorBy` | A choice field whose option colours the item, resolved through `OptionColors` below |
| `related` | `"reference"`, `"semantic"`, or `false`. `"reference"` unless set |
| `readingTime` | Shows a read time worked out from the body |
| `layout` | `"list"` or `"article"`. `"list"` unless set |
| `index` | Also takes an object of the index's own words (`eyebrow`, `heading`, `lede`, `empty`, `unavailable`), which turns the index on |
| `indexPage` | A site path whose page's blocks are drawn above the list on the index |
| `tree` | Turns the collection into a documentation manual: field names for `section`, `order`, `parent` and `product`, plus `sections`, `products` (each may carry a `note`), `searchPath`, `searchIndex`, `variant`, `editBase`, `editPath` and `limit` for the sidebar and product switcher |

### OptionColors

Keyed by `type.field`, then by option, naming a colour from `Colors`. The `events` collection above
colours its `EntryType` choice field this way:

```json
{ "project.AreaOfFocus": { "Providing clean water": "sky", "Supporting education": "gold" }, "event.EntryType": { "Fundraiser": "gold", "Outreach": "sky" } }
```

### OptionStyles

Keyed like `OptionColors`, by `type.field` and then by option, each option an object with an optional
`tone`, `icon` and `label`:

```json
{ "project.AreaOfFocus": { "Providing clean water": { "tone": "sky", "icon": "location", "label": "Water" } } }
```

`tone` names a colour as `OptionColors` does, `icon` is one of the renderer's icon names, and `label`
is the word a visitor reads in place of the option's value. A style for an option wins over its
`OptionColors` entry, field by field. See
[Collections](https://github.com/BaryoDev/barakoPress/blob/master/docs/collections.md).

### Labels

An object of label key to the words printed, for the copy the renderer's own screens carry:

```json
{ "minRead": "minutong pagbasa", "by": "ni", "related": "Kaugnay" }
```

A key left out keeps the renderer's English. The keys are the renderer's, listed in
[Sites](https://github.com/BaryoDev/barakoPress/blob/master/docs/sites.md).

### Variants

```json
[ { "name": "pine", "label": "Pine", "colors": { "accent": "#1A6B41" } } ]
```

Each variant overrides colours only. A visitor's choice is remembered in their browser.

### TopBar, HeaderLinks, FooterColumns, SocialLinks

```json
{ "text": "City of Koronadal, South Cotabato", "links": [ { "label": "Facebook", "href": "https://facebook.com/rckoronadal" } ] }
```

```json
[ { "label": "Donate", "href": "/donate" } ]
```

```json
[ { "heading": "Club", "links": [ { "label": "About", "href": "/about" } ] } ]
```

```json
[ { "network": "facebook", "href": "https://facebook.com/rckoronadal" } ]
```

An `href` is either a path on the site or an absolute http or https URL. The renderer drops any other
scheme.

A header link may also carry `activeOn`, space separated site paths it is current on, and
`children`, links drawn as a dropdown one level deep:

```json
[ { "label": "Docs", "href": "/docs", "activeOn": "/docs /guides", "children": [ { "label": "API", "href": "/docs/api" } ] } ]
```

### MenuLinks and HeaderActions

`MenuLinks` has the shape of `HeaderLinks` and holds the phone menu's rows. Unset or empty, the phone
menu shows `HeaderLinks`. `HeaderActions` is up to four links after the header links, each with a
`variant` of `primary`, `secondary` or `plain`; unset or unknown is `primary`.

```json
[ { "label": "Donate", "href": "/donate", "variant": "primary" }, { "label": "Contact", "href": "/contact", "variant": "plain" } ]
```

The barakoPress README has the rest, in [The built-in header](https://github.com/BaryoDev/barakoPress#the-built-in-header).

### Tokens and Tones

```json
{ "accent": "#E4572E", "cms-ink": "#1D3A8A", "cms-bg": "#E8EEFD", "gutter": "24px", "serif": "'Zilla Slab', Georgia, serif" }
```

```json
{ "cms": { "ink": "cms-ink", "bg": "cms-bg", "edge": "#B9C8F5" } }
```

A token is a name and one value: a colour, a CSS length or a font stack. The renderer emits each one
as `--t-<name>`. A tone is a name and three colours, `ink`, `bg` and `edge`, each a token name, a
`Colors` slot or a colour written out. Block tone fields, `HeaderTone` and `FooterTone` included,
offer the site's tones after the built-in six. The renderer drops a token or tone that fails its
check and keeps the rest. See [Tokens and tones](https://github.com/BaryoDev/barakoPress#tokens-and-tones).

### StyleRecipes

Keyed by a recipe name, each with an optional `class` and an optional `style` of CSS property to
value. `{name}` in a value stands for a token, and `{colors.<slot>}`, `{space.<step>}` and the like
for the theme's own values.

```json
{ "card": { "class": "lift", "style": { "padding": "22px 24px", "background": "{colors.surface}", "border-radius": "16px" } } }
```

The renderer keeps a fixed list of properties and a narrow value shape, and drops what falls
outside them. See [Style recipes](https://github.com/BaryoDev/barakoPress#style-recipes).

### Plugins

A list of plugin names, from the plugins the deployment's barakoPress image has installed.

```json
[ "tally" ]
```

Unset, the renderer uses the plugins its own config names. Saved as an empty list, every plugin is
off for this tenant. See [Plugin packages](https://github.com/BaryoDev/barakoPress#plugin-packages).

## Holding a site back

`Mode` set to `Holding` asks a frontend to show the page at `HoldingPath` on every route instead of
the site. It covers a launch, maintenance and a seasonal break. The holding page is an ordinary page
from the Pages module, and `HoldingPath` is a path string rather than a reference because a
blueprint may only reference types it declares. Going live again is a publish, not a deploy.

`Mode` is a string field, not a choice field, documented as `Live` or `Holding`. Any other value
should be read as `Live`.

**Mode and HoldingPath are presentation, not access control.** Frontends honour them; the API
hides nothing. While holding, published, publicly deliverable content is still served from
`/api/public/...` to anyone who asks. To keep content hidden before launch, leave it unpublished and
schedule the publish ([scheduling.md](scheduling.md)).

### Share links

People who need to see a held site (a client, a board, a reviewer) get a share link. A tenant can
have many, each with its own label and expiry, and each revoked on its own.

A link is:

```
{site Url}/_share#{key}
```

The key is in the fragment, so a browser never sends it to a server: it stays out of access logs
and out of the `Referer` header. The frontend's `/_share` page reads the fragment and redeems it.

| Route | Who | Answers |
| :--- | :--- | :--- |
| `POST /api/site/share-links` | may update `site` | 201 `{ id, label, expiresAt, createdAt, key }` |
| `GET /api/site/share-links` | may update `site` | a page of `{ id, label, createdAt, createdBy, expiresAt, revokedAt, lastUsedAt }`, with `maxExpiryDays` beside `items` |
| `DELETE /api/site/share-links/{id}` | may update `site` | 204, or 404 for an unknown id |
| `POST /api/public/site/share-links/redeem` | anyone | 200 `{ expiresAt }`, or 404 |

Managing links needs update permission on the `site` type (SuperAdmin always has it), for listing
too, since the list names who shared the site with whom.

**Creating.** The body is `{ "label": "...", "expiresAt": "..." }`. The label is required, at most
100 characters. `expiresAt` is optional: unset means 30 days from now, and more than the maximum
(90 days) away is a 400. The key is 32 random bytes, base64url encoded, and appears in this response
and nowhere else. Only its SHA-256 is stored, on a tenant scoped document that is not part of site
settings, public delivery or a portability export. Creating is audited as `site.share_link.created`
with the label and expiry, never the key. A tenant holds at most 100 active links; revoke one to
make another.

**The maximum expiry.** The list response carries `maxExpiryDays` on the page itself, next to
`items` and `totalItems`, so it is there when the tenant has no links yet. It is the longest expiry
create accepts, in whole days, read from the same constant the create validator checks. A client
should build its expiry choices from it rather than keep its own number. An API older than this
field sends none, and 90 is what those enforce. An `expiresAt` of now plus `maxExpiryDays` days is
accepted: the time the request takes only moves the comparison later, and the validator allows one
minute past the maximum for a client whose clock runs ahead of the server's. A client whose clock is
more than a minute ahead is refused at the maximum, so one that cannot trust its clock should leave
a margin.

**Redeeming.** The frontend posts `{ "key": "..." }` with the tenant resolved the same way as
`GET /api/public/site`. A live link answers 200 with its `expiresAt` and records `lastUsedAt`. A
wrong key, an expired or revoked link, another tenant's key and the key of a link to one entry or
page (below) all answer the same 404. Both answers carry `Cache-Control: no-store`. Redeeming is
rate limited per tenant and visitor (`RateLimiting:SiteShare`, 10 a minute by default). The key is
never logged.

**Sessions.** After a 200 the frontend may keep its own session so the previewer does not redeem on
every page. Keep it for at most 24 hours, and never past the link's `expiresAt`; after that, redeem
again.

**Revoking.** `DELETE` sets `revokedAt`, is audited as `site.share_link.revoked`, and the key stops
redeeming at once. A session a frontend already started runs out on its own, within 24 hours.

### Links to one entry or one page

A link can also open one entry, whatever its status, so a client reviews one draft without the
whole held site opening. The same document, key, expiry, revocation and audit rows as a link
to the site; what differs is what the link names, who may make it and the hash it is stored under.

| Route | Who | Answers |
| :--- | :--- | :--- |
| `POST /api/contents/{id}/share-links` | may update that entry | 201 `{ id, label, expiresAt, createdAt, scope, path, key }` |
| `GET /api/contents/{id}/share-links` | may update that entry | a page of the entry's links, each with `scope` and `path`, and `maxExpiryDays` beside `items` |
| `DELETE /api/contents/{id}/share-links/{linkId}` | may update that entry | 204, or 404 for a link that is not this entry's |
| `POST /api/public/site/share-links/open` | anyone | 200 `{ scope, expiresAt, path, entry }`, or 404 |

**Creating.** The body is `{ "label": "...", "expiresAt": "...", "path": "..." }`, with the same
label and expiry rules as a link to the site. Without `path` the link's `scope` is `entry`. With
`path` it is `page`: the path the page is served at, starting with `/` and at most 2048 characters.
It is refused if it holds an empty segment (`//` anywhere), a `.` or `..` segment, a backslash,
`?`, `#`, `%`, whitespace, a control character or a Unicode format character such as a bidi
override, so it cannot point off the site, even after a frontend tidies it, and cannot read as
something it is not. The API stores the path
and hands it back on open; it never looks anything up by it, so a page that moves keeps the path its
link was made with. A link to an entry that could not be delivered (its type is not publicly
deliverable, or the entry is not `Public`) is a 400, since it would open nothing. An entry holds at
most 20 active links, and they do not count toward the site's 100.

**Opening.** The frontend posts `{ "key": "..." }`. The key is in the body, so it is in no URL. The
answer says what the key is for:

- `scope: "site"`: a link to the whole site. No `entry`. The same meaning as a 200 from redeem.
- `scope: "entry"` or `"page"`: `entry` is that one entry in the shape `GET /api/public/{type}/{slug}`
  returns, and `path` is set for a page link.

What an entry or page link opens is that one entry and nothing else:

- Only fields the type marks `Public`. A link is not a signed-in caller.
- Not an entry whose document sensitivity is not `Public`, and not an entry of a type that is not
  publicly deliverable. Both answer 404, as does a link whose entry was deleted.
- No other entry. The request names no entry, so there is no id to change. Reference fields come
  back as the ids they are stored as; a referenced entry, a child page or a parent is read through
  the ordinary delivery routes, which serve it only if it is published.
- Nothing on another tenant: the key is looked up in the resolved tenant.
- Not the whole held site: redeem answers 404 for this key.

A wrong key, an expired or revoked link, another tenant's key, a preview token (below) and a link
that opens nothing all answer the same 404 with the same body. Every answer from redeem and open carries
`Cache-Control: no-store`, a 429 included, so no shared cache holds an entry a link opened. Open
shares redeem's rate limit. `lastUsedAt` is recorded on a 200 only.

**Managing.** Listing and revoking go through the entry, and need update on it. A link to the site
is not listed or revoked there, and an entry's link is not listed or revoked under
`/api/site/share-links`. Both are audited under the same two action names, with `scope` and
`entryId` beside the label. An API key reaches none of the share link routes, whatever its scopes:
the three under `/api/contents/{id}/share-links` answer a key 403, as `/api/site/share-links` does.
A link is a credential for someone with no account, and one a key made would outlive the key.

**Erasing.** Erasing an entry (`DELETE /api/contents/{id}/erase`) deletes its links and preview
tokens in the same transaction.

**Stored links.** A link made before scopes existed has neither an entry nor a path and is a link
to the site, as it always was. No schema change: the three new fields are in the document body and
none is indexed.

**Two builds on one database.** A link to the site is stored under the SHA-256 of its key, as
before. An entry link, a page link and a preview token are stored under a different hash (the
SHA-256 of one `0xFF` byte followed by the key), which no key sent to redeem can produce. So a build
from before these links, running beside this one during a rolling deploy or after an image
rollback, cannot redeem one of them as a link to the whole site. What that older build does see:
`GET /api/site/share-links` lists the rows as if they were links to the site,
`DELETE /api/site/share-links/{id}` can revoke them, and they count toward its 100.

### Preview tokens

`POST /api/preview` is deprecated. Its body and status codes have not changed, but the token is now
the key of an entry link that lasts 30 minutes, stored hashed like any other and deleted with its
entry. It is the one kind of link accepted in the `?preview=` query of
`GET /api/public/{type}/{slug}`: a query string reaches access logs, so a key that can last 90 days
is not taken from one. It is accepted nowhere else: open and redeem both answer 404 for it. A caller
needs `read` on the entry to get a token, as before.

Because `read` is all it takes and the route used to store nothing, a token leaves little behind:

- It is not in `GET /api/contents/{id}/share-links` and cannot be revoked there. It is gone in 30
  minutes.
- Minting writes no audit row.
- Its stored label is fixed, so the row holds nothing of the entry but its id.
- An entry keeps at most 20 live tokens. A mint past that deletes the entry's oldest to make room,
  so the oldest of 21 previews stops working early. Each mint also deletes the tenant's expired
  tokens. Neither counts toward the entry's 20 links.

The token follows its entry: if the entry's slug is renamed inside the 30 minutes, the token works
at the new slug and not the old one. At any slug but its entry's it is ignored and the read is the
ordinary published one.

The `Deprecation` header (`@1790899200`, RFC 9745) is on what the route's handler answers: the 200,
its 404s, and the 401 for a token whose user no longer exists. It is not on the 401 for no
credentials, a 400 for a body that does not bind, a 429 or the 403 an API key gets.

## Why a content type

Decided on #793: a singleton reuses what content already has, publishing, history, per-field
permissions and delivery, and barakoBrew already edits singletons as one screen. A dedicated
endpoint could refuse an unreadable colour pair at the API, and that check lives in the barakoBrew
Theme screen instead (BaryoDev/barakoBrew#134).
