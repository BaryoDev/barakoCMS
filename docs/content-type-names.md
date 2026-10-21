# Content type names

A content type name is matched ignoring case when you look the type up: `Article` and `article`
find the same type. An entry stores the name exactly as the caller spelled it. `POST /api/contents`
with `contentType: "Article"` against a type stored as `article` writes an entry whose
`contentType` is `Article`.

This is deliberate. The stored name reaches modules through the `Content` document, and some match
it exactly (the accounting module reads `journalEntry`). Rewriting it to the type's own spelling
would change what those readers see and leave old entries in the old spelling next to new ones.

What that means for you:

- Case is ignored when a type is looked up by name, and `GET /api/contents?contentType=` finds the
  type that way. It then lists entries stored under the type's own spelling and under the
  spelling you asked with, so `contentType=article` lists entries stored as `article`, and `Article`
  too when the type is stored as `Article`. An entry stored in some third spelling is listed only
  when you ask with that spelling. The filter compares the stored value exactly so it can use the
  index.
- Each entry still reports the spelling it was written with.
- Code that reads `contentType` off an entry and compares it exactly should compare ignoring case,
  or use the spelling your own writers send.
- Two types whose names differ only by case cannot both exist (#1158).
