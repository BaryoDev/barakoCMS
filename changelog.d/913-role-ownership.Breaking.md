- **A role made for one client was a role of every client.** Roles were one list for the
  deployment, so a custom role held in two tenants was edited for both by whoever could edit it. A
  role now has an owner: `TenantSlug` for a tenant's role, or `IsPlatform` for a role seeded for
  the whole deployment (SuperAdmin, Admin, User, the demo HR role, and a module's role seeded under
  its fixed id). `migrations/5.0.0/role-tenancy.sql` gives every stored role one: a role held by the
  memberships of one tenant moves to it, one held in several is copied into each other tenant under
  a derived id with that tenant's memberships and field role lists rewritten to the copy, and one
  held by nobody, or as a global role on an account, stays with the default tenant. Nobody's
  permissions change. The unique index on a role's name becomes one on the tenant and the name, so
  the file must run before 5.0.0 starts. The new `manage_platform_roles` capability covers platform
  roles; only SuperAdmin holds it by default (#913).
