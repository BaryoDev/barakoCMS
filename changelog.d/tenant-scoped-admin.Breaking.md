- **A tenant's administrator sees that tenant's data on the admin screens backed by a global
  table.** Client errors, email events and PWA installs are stored once for the deployment, and
  `GET /api/client-errors`, `GET /api/email-events` and `GET /api/pwa/installs` listed every
  tenant's rows to any Admin. They now list the current tenant's rows, and a SuperAdmin still sees
  every tenant's. An email event counts for a tenant when its recipient is an active member there.
  `POST /api/client-errors/{id}/resolve` answers 404 for another tenant's error. The same fault
  reported from two tenants is now two rows, and a report that names no tenant is kept on the
  tenant the request resolved to. `/api/settings`, `GET /api/settings/email` and everything under
  `/api/feature-flags/admin` configure every tenant at once, so they now answer 403 unless
  `manage_settings` or `manage_feature_flags` comes from one of the caller's global roles. An Admin
  whose role is global, as on a single-tenant deployment, keeps all of them; an Admin through a
  tenant membership does not. These requests used to succeed, so this shares the move of
  `X-Api-Contract-Version` to 5 with the global-roles change.
