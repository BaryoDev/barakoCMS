- **An Admin could grant itself the SuperAdmin role.** `POST /api/users/{id}/roles` is reachable
  with `manage_user_membership`, which the Admin role holds, and it assigned any role including
  SuperAdmin with no check, so an Admin stepped outside the capability model entirely. Granting
  SuperAdmin now requires the caller to already be SuperAdmin, matching the guard the per-tenant
  membership endpoint already had.
