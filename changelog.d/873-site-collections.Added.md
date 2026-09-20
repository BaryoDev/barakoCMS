- **The `site` blueprint declares `Collections`, the setting barakoPress reads to render a tenant's
  configured lists.** Each entry names a content type, a route, a field map, references, a sort, and
  how the list behaves: whether it feeds, whether it is in the sitemap, a page size, a label and a
  noun, related items, a read time, and a choice field to colour it by (BaryoDev/barakoPress#5). A
  fresh tenant applying the `site` blueprint gets the field; a tenant that applied it earlier adds it
  by hand with `POST /api/content-types/site/fields`, the same as any field added since. Nothing
  changes for a tenant that leaves it unset. Additive, so `ApiContract.Version` does not move.
  (closes #873)
