- **Only a platform administrator changes a user's global roles.** `POST /api/users/{id}/roles`
  and `DELETE /api/users/{id}/roles/{roleId}` change the roles a user holds in every tenant. They now
  answer 403 unless the caller's `manage_user_membership` comes from one of the caller's own global
  roles, so an Admin whose role comes from a tenant membership can no longer use them and manages
  roles inside that tenant through `/api/tenants/members`. Removing SuperAdmin now takes a
  SuperAdmin, and removing it from the last user who holds it answers 409. These requests used to
  succeed, so `X-Api-Contract-Version` moves to 5, and the barakoBrew console has to accept contract
  5 before it runs against this release.
