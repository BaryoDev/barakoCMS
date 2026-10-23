# BarakoCMS.Tagging

One vocabulary to categorize any record. A vocabulary holds terms, terms can nest up to 8 levels, and a term can be applied to any record by content type and id. Everything is scoped to the calling tenant.

```bash
dotnet add package BarakoCMS.Tagging
```

The module does not look up the record it tags. The content type and record id are plain strings, so the same term can sit on a content entry, a file or anything else with an id.

## Endpoints

| Route | Capability |
|---|---|
| `POST/GET /api/taxonomy/vocabularies` | `manage_taxonomy` / `view_taxonomy` |
| `PUT/DELETE /api/taxonomy/vocabularies/{id}` | `manage_taxonomy` |
| `POST/GET /api/taxonomy/vocabularies/{vocabularyId}/terms` | `manage_taxonomy` / `view_taxonomy` |
| `PUT/DELETE /api/taxonomy/terms/{id}` | `manage_taxonomy` |
| `POST /api/taxonomy/records/{contentType}/{recordId}/terms` | `assign_terms` |
| `DELETE /api/taxonomy/records/{contentType}/{recordId}/terms/{termId}` | `assign_terms` |
| `GET /api/taxonomy/records/{contentType}/{recordId}/terms` | `view_taxonomy` |
| `GET /api/taxonomy/terms/{termId}/records` | `view_taxonomy` |

The Admin role holds all three capabilities by default. Lists are paged.

## Two record types, one tag

```bash
# a vocabulary and a term
POST /api/taxonomy/vocabularies                        {"key":"topics","name":"Topics"}
POST /api/taxonomy/vocabularies/{id}/terms             {"key":"priority","name":"Priority"}

# tag a contact and a file with it
POST /api/taxonomy/records/contact/{contactId}/terms   {"termId":"{termId}"}
POST /api/taxonomy/records/file/{fileId}/terms         {"termId":"{termId}"}

# both come back, narrow with ?contentType=file
GET  /api/taxonomy/terms/{termId}/records
```

## Behaviour

- Keys are lowercase slugs. A vocabulary key is unique per tenant, a term key is unique per vocabulary. A repeat is 409.
- Applying a term that is already on the record changes nothing.
- `?includeDescendants=true` on the records query also returns records tagged with child terms.
- On term update, a null `parentId` moves the term to the top level.
- A term with children cannot be deleted (409). Deleting a term or vocabulary removes its assignments.
