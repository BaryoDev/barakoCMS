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
| `Logo`, `FooterLogo`, `Favicon`, `ShareImage` | url | Uploaded files or any absolute URL |
| `LogoAlt` | string | Alt text for the logo |
| `Colors` | json | Named colours |
| `Fonts` | json | Font families by role |
| `Radii` | json | Corner radii |
| `Layout` | json | Content widths |
| `OptionColors` | json | A colour per option of a choice field |
| `Variants` | json | Themes a visitor can switch between |
| `TopBar` | json | The strip above the header |
| `HeaderLinks` | json | Links in the header beyond the page tree |
| `FooterColumns` | json | Footer link columns |
| `SocialLinks` | json | Social profiles |
| `Copyright` | string | The footer's copyright line |
| `Mode` | string | `Live` or `Holding`. Unset means `Live`. See [Holding a site back](#holding-a-site-back) |
| `HoldingPath` | string | The site path of the page shown while holding, such as `/holding` |

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

### OptionColors

Keyed by `type.field`, then by option, naming a colour from `Colors`.

```json
{ "project.AreaOfFocus": { "Providing clean water": "sky", "Supporting education": "gold" } }
```

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

## Holding a site back

`Mode` set to `Holding` asks a frontend to show the page at `HoldingPath` on every route instead of
the site. It covers a launch, maintenance and a seasonal break. The holding page is an ordinary page
from the Pages module, and `HoldingPath` is a path string rather than a reference because a
blueprint may only reference types it declares. Going live again is a publish, not a deploy.

`Mode` is a string today, documented as `Live` or `Holding`. Any other value should be read as
`Live`. It becomes a choice field once the choice type (#820) is on master.

**Mode and HoldingPath are presentation, not access control.** Frontends honour them; the API
hides nothing. While holding, published, publicly deliverable content is still served from
`/api/public/...` to anyone who asks. To keep content hidden before launch, leave it unpublished and
schedule the publish.

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
| `GET /api/site/share-links` | may update `site` | a page of `{ id, label, createdAt, createdBy, expiresAt, revokedAt, lastUsedAt }` |
| `DELETE /api/site/share-links/{id}` | may update `site` | 204, or 404 for an unknown id |
| `POST /api/public/site/share-links/redeem` | anyone | 200 `{ expiresAt }`, or 404 |

Managing links needs update permission on the `site` type (SuperAdmin always has it), for listing
too, since the list names who shared the site with whom.

**Creating.** The body is `{ "label": "...", "expiresAt": "..." }`. The label is required, at most
100 characters. `expiresAt` is optional: unset means 30 days from now, and more than 90 days away is
a 400. The key is 32 random bytes, base64url encoded, and appears in this response and nowhere else.
Only its SHA-256 is stored, on a tenant scoped document that is not part of site settings, public
delivery or a portability export. Creating is audited as `site.share_link.created` with the label
and expiry, never the key. A tenant holds at most 100 active links; revoke one to make another.

**Redeeming.** The frontend posts `{ "key": "..." }` with the tenant resolved the same way as
`GET /api/public/site`. A live link answers 200 with its `expiresAt` and records `lastUsedAt`. A
wrong key, an expired or revoked link, and another tenant's key all answer the same 404. Both
answers carry `Cache-Control: no-store`. Redeeming is rate limited per client IP
(`RateLimiting:SiteShare`, 10 a minute by default). The key is never logged.

**Sessions.** After a 200 the frontend may keep its own session so the previewer does not redeem on
every page. Keep it for at most 24 hours, and never past the link's `expiresAt`; after that, redeem
again.

**Revoking.** `DELETE` sets `revokedAt`, is audited as `site.share_link.revoked`, and the key stops
redeeming at once. A session a frontend already started runs out on its own, within 24 hours.

## Why a content type

Decided on #793: a singleton reuses what content already has, publishing, history, per-field
permissions and delivery, and barakoBrew already edits singletons as one screen. A dedicated
endpoint could refuse an unreadable colour pair at the API, and that check lives in the barakoBrew
Theme screen instead (BaryoDev/barakoBrew#134).
