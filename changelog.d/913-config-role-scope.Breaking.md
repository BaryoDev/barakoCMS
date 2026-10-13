- **A config document's roles reached every tenant's roles.** The `roles` and `permissionRules`
  sections of `GET /api/config/export` and `POST /api/config/plan` and `/apply` were written while
  roles were shared, so one tenant's export listed every tenant's custom roles and an apply could
  change another tenant's role by its name. They now follow the roles routes: a tenant exports its
  own roles and the platform roles, a name is read as the tenant's own role and then a platform
  role, a name it does not reach creates a role of that tenant, and changing a platform role or its
  rules, or adding a capability that reaches past the tenant, needs `manage_platform_roles`.
  Writing a tenant's own roles takes `manage_roles` in that tenant, from a membership too. A seed
  file's roles go under the tenant's `config` block and belong to that tenant, and its members' roles
  are found among that tenant's own roles and the platform roles (#913).
