- **Workflows, permission rules, connectors and the site entry could not be promoted with a
  tenant's content types.** The config document gains four sections. `workflows` holds each
  workflow by name; a credential parameter is never exported, a document carrying one is refused,
  and a change keeps a stored credential only on an action whose position, type and other
  parameters stay the same; otherwise it is dropped, destructively. `permissionRules` holds what each
  role may do with each of the tenant's own content types, under `role/type`. `connectors` holds
  each connector by slug with its secrets by name only: a document that carries a secret's value is
  refused, a name the tenant does not hold is a note to set it with `PUT /api/connectors/{slug}`,
  and moving a connector to another origin clears its secrets, destructively. `siteSettings` holds
  the tenant's `site` entry, written for the caller the way the content routes write it. A workflow,
  rule or connector the document leaves out is removed, destructively (#921).
