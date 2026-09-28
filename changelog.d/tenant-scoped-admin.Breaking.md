- **A tenant's administrator sees that tenant's data on the admin screens backed by a global
  table.** Client errors, email events and PWA installs are stored once for the deployment, and
  `GET /api/client-errors`, `GET /api/email-events` and `GET /api/pwa/installs` listed every
  tenant's rows to any Admin. A caller holding the capability through a tenant membership now sees
  the current tenant's rows; one holding it through a global role, as on a single-tenant
  deployment, still sees every tenant's. `POST /api/client-errors/{id}/resolve` answers 404 for
  another tenant's error. An email event now carries the tenant that sent the email, recorded
  against Resend's id when it is sent and kept 30 days (a new `sent_emails` table, created on an existing database by
  `migrations/4.5.0/email-sent-emails.sql`); an event with
  no recorded sender is visible through a global role only. Only mail sent on a tenant's behalf is
  recorded, which today is a workflow's email: a user's own account mail (sign-in codes,
  verification, lockout notices) belongs to no tenant. `IEmailService` gains `SendForTenantAsync`,
  whose default ignores the tenant, so an existing provider keeps working. The same fault reported from two
  tenants is now two rows, a report that names no tenant takes the tenant the request resolved to,
  and reported tenants are stored lowercased. `/api/settings`, `GET /api/settings/email`,
  everything under `/api/feature-flags/admin` and all six `/api/analytics` routes are shared by
  every tenant, so they now answer 403 to a caller holding `manage_settings`,
  `manage_feature_flags`, `view_analytics` or `manage_analytics_websites` through a membership
  only. These requests used to succeed, so this shares the move of `X-Api-Contract-Version` to 5
  with the global-roles change.
