- **The core routes still gated on a role name are pinned, so the list can only shrink.** #443 has
  been migrating them to capabilities area by area, and the areas it listed are done. What it did not
  have is anything stopping a new one appearing: two were added while the migration was in progress,
  in #185 and #111, and nothing noticed.

  `CoreRoleGateInventoryTests` records the remaining set exactly and fails in both directions. A new
  role-name gate fails it, which is the case that went unnoticed twice. Migrating one fails it too,
  until the route comes out of the list, which is a deliberate prompt to check the capability reached
  `SystemCapabilities` and Admin's defaults.

  The two that were added during the migration are migrated here. `GET /api/modules` asks for
  `view_modules`, named for reading because it answers with two fields per module and manages
  nothing. `POST /api/content-types/{name}/seo-fields` asks for `manage_content_types`, since adding
  fields to a content type is exactly what that capability is, rather than inventing a name for one
  endpoint. Admin holds both, matching what it reached before.

  Nothing else changes. `Roles(...)` is FastEndpoints' own authorization and keeps working; what it
  cannot do is admit a role somebody created, which is the point of #443.
