- **One tenant's role editor could edit every tenant's roles.** The roles routes listed, read,
  changed and deleted any role in the deployment, so a role carrying `manage_roles` given to one
  client's administrator reached every client's roles, the shared Admin role included. The roles
  routes and the member role routes now reach the caller's tenant's roles and the platform roles:
  `GET /api/roles` lists only those (a holder of `manage_platform_roles` sees all), another
  tenant's role answers 404, and changing or deleting a platform role such as Admin needs
  `manage_platform_roles` and answers 403 without it. `POST /api/roles` makes a role of the
  caller's tenant, or a platform role with `isPlatform: true`; a name already used there is a 409.
  Without `manage_platform_roles`, a role write cannot add a capability that reaches past the
  tenant. A tenant's sign-up and MFA roles follow the same rule. The role response carries
  `tenantSlug` and `isPlatform`. `ApiContract.Version` moves to 8 with #894 (#913).
