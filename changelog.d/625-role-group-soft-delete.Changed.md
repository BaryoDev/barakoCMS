- **Deleting a role or a user group is recoverable.** `DELETE /api/roles/{id}` and
  `DELETE /api/user-groups/{id}` mark the item deleted instead of removing it. A deleted role grants
  nothing and cannot be listed, read, edited or assigned. `GET .../deleted` lists them, `POST
  .../{id}/restore` brings one back, and `DELETE .../{id}/erase` removes a deleted one for good.
  Creating a role with the name of a deleted one erases the deleted one, as before. Role and group
  responses gain an optional `deletedAt` (#625).
