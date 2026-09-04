- **The last two core routes on a role name ask for a capability, and the count is pinned at zero.**
  `GET /api/modules` asks for `view_modules`, named for reading because it answers with two fields
  per module and manages nothing. `POST /api/content-types/{name}/seo-fields` asks for
  `manage_content_types`, since adding fields to a content type is exactly what that capability is,
  rather than inventing a name for one endpoint. Admin holds both by default, matching what it
  reached before.

  Both were added while #443 was in progress, in #185 and #111, and nothing noticed. `RoleGateTests`
  now asserts that no core route gates on a role name, counting a route that carries both a
  capability and a role list, so the next one fails the suite instead of waiting for a reader.
