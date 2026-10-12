- **A tenant switcher could not show which role the caller holds in each tenant.** Every item of
  `GET /api/me/tenants` now carries `roles`, the roles the caller holds in that tenant (the
  membership's roles plus their global ones) as `{ id, name }`. Additive.
