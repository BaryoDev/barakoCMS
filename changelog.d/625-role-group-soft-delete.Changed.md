- **Deleting a role or a user group is recoverable.** `DELETE /api/roles/{id}` and
  `DELETE /api/user-groups/{id}` mark the item deleted instead of removing it. A deleted role grants
  nothing and cannot be listed, read, edited or assigned. A deleted group stops granting its roles
  to its members at once, and restoring it grants them again. `GET .../deleted` lists them, `POST
  .../{id}/restore` brings one back, and `DELETE .../{id}/erase` removes a deleted one for good.
  Creating a role with the name of a deleted one erases the deleted one in the same save, as a
  create after a delete always worked. Role and group responses gain an optional `deletedAt` (#625).
  Before rolling back to a release without this change, erase every deleted role
  (`GET /api/roles/deleted`, then `DELETE /api/roles/{id}/erase` for each), because an older
  release ignores the deleted mark and the roles would grant again. Do the same for groups
  (`GET /api/user-groups/deleted`) and queries (`GET /api/queries?deleted=true`).
