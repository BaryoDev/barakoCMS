# SEO fields

A content type has no meta title, meta description, canonical URL or social image until it asks for
them. Without a shared set, every agency invents its own naming convention and every frontend
re-implements the tags against a different one.

## Opting a content type in

```
POST /api/content-types/{name}/seo-fields
```

Adds five fields to the type. Additive and idempotent: a field the type already has is reported and
left exactly as it was, because a client may well have renamed the display name or made one
required, and none of that is this endpoint's to undo.

| Field | Type | What it is |
| :--- | :--- | :--- |
| `MetaTitle` | string | What a search result and a browser tab say |
| `MetaDescription` | text | The snippet under the title |
| `CanonicalUrl` | url | The one URL this content should be indexed under |
| `SocialImage` | url | The sharing image |
| `NoIndex` | bool | Ask search engines to skip this entry |

They are ordinary fields marked Public, not a separate structure. That is what makes the delivery
side nearly free: they are validated, delivered, searched and scrubbed by everything that already
handles a field.

All five are optional. Opting a type in must not make every existing entry invalid, and the
fallbacks below are what make an empty value fine.

## Reading them

Every public delivery response carries a resolved `seo` block, or omits it entirely when the type has
not opted in.

```json
{
  "id": "...",
  "slug": "spring-roast-notes",
  "data": { "...": "..." },
  "seo": {
    "title": "Spring roast notes | Barako Coffee",
    "description": "What changed in the March lot.",
    "canonicalUrl": null,
    "imageUrl": "https://cdn.example.com/roast.jpg",
    "noIndex": false
  }
}
```

Resolved rather than left to the caller, and that is the point of it existing. The raw fields are
already in `data`; a frontend reading them itself would have to know the names and re-implement the
fallback, and two frontends would do it two ways.

Null rather than an empty object when the type has not opted in, so a caller cannot mistake "this
type has no SEO fields" for "this entry has not filled them in".

## The fallback

**An unset meta title resolves to the entry's own title.** An empty title tag is worse than no tag:
a search engine shown one indexes the page with nothing to display, whereas a page with no tag gets
a title chosen from its content.

The entry title is found under `Title`, `Name`, `DisplayName`, `Label`, `Subject` or `Heading`, in
that order, which is exactly the list the admin uses to label an entry. Two lists would answer
differently the first time one of them gained a name, and the symptom is a page whose tab and whose
search result disagree.

Only a genuinely titleless entry resolves to `null`, and a `null` is absent from the JSON, so a
frontend renders no tag at all.

A meta title of only spaces counts as unset. An editor clearing the box leaves an empty string
rather than a missing key.

## Lengths

`MetaTitle` is usually cut around 60 characters and `MetaDescription` around 155. Those are
**guidance, not validation**, and deliberately not enforced: search engines truncate on pixel width
rather than character count, so a hard limit would be wrong in both directions, refusing a title that
displays fine and accepting one that does not. The admin shows the count and a preview; the decision
stays with the person who can see the words.

## NoIndex and the sitemap

An entry with `NoIndex` set is left out of `/api/public/sitemap.xml`.

The tag on the page is the instruction a crawler obeys. The sitemap is the invitation. Listing a page
and then telling the crawler to go away when it arrives wastes its budget on the site, and Search
Console reports the contradiction as an error, which reads as a broken sitemap rather than a
deliberate choice.

It is still delivered through the API, because the frontend needs it to render the `robots` tag.
