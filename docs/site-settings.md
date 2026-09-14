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

## Why a content type

Decided on #793: a singleton reuses what content already has, publishing, history, per-field
permissions and delivery, and barakoBrew already edits singletons as one screen. A dedicated
endpoint could refuse an unreadable colour pair at the API, and that check lives in the barakoBrew
Theme screen instead (BaryoDev/barakoBrew#134).
