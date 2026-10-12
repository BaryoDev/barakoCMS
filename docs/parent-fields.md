# Parent fields

A `reference` field that points at its own type may declare itself the entry's parent:

```json
{ "name": "Parent", "type": "reference", "referenceType": "category", "parent": { "maxDepth": 8 } }
```

Every create, update and rollback of the type then walks the parent chain and refuses, with a 400,
a value that would make the entry its own parent or ancestor, or that would put the entry, or an
entry below it, more than `maxDepth` levels deep. No module or code registers anything for it: the
declaration is part of the type, so each tenant can have its own trees.

- One field per type, a single reference (not `multiple`) whose `referenceType` is the type itself.
- `maxDepth` is 1 to 64, and 64 when left out.
- Set when the type is created or the field is added. Declaring it on an existing field is a field
  change, which belongs to the change-field endpoint (#934).
- The walk runs inside the write's transaction under an advisory lock on the tenant, type and
  field, so two saves at once cannot close a loop between them.
- A reference to the type itself that does not declare `parent` is not a parent, and is checked as
  it always was: a "related post" pointing back at a post that points forward is fine.

The Pages module reads its page type and fields per tenant (`GET` and `PUT /api/pages/settings`,
see the module's README). When the page type declares its parent field this way, the module leaves
the walk to core.
