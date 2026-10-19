- **Deleting a saved query is recoverable.** `DELETE /api/queries/{slug}` now marks the query
  deleted instead of removing it. `GET /api/queries?deleted=true` lists them, `POST
  /api/queries/{slug}/restore` brings one back, and `DELETE /api/queries/{slug}/erase` removes a
  deleted query for good. Saving over a deleted slug still works and replaces it. Roles, groups,
  connectors and requests are still hard deletes (#625).
