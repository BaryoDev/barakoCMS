- **A switched-off tenant kept serving its published content.** A tenant had only `IsActive`, and
  resolution never read it, so a deactivated tenant's pages stayed up by header and by subdomain,
  and its custom domains fell through to the default tenant. A tenant now has a `Status`, `Active`,
  `Suspended` or `Archived`, read once at resolution in every tenancy mode. For a tenant that is not
  Active, delivery (`/api/public/*`) answers 404, a caller signed in to that tenant gets 403 on every
  other route, and anyone else gets 404. Its scheduled content and collection syncs wait until it is
  Active again, and a scheduled publish whose unpublish time also passed meanwhile is archived
  without being published. `PUT /api/tenants/{handle}` takes `status`, the tenant response carries
  it, and `isActive` stays on both, derived from it. `Tenant.IsActive` is obsolete, removal planned
  for 6.0. `migrations/5.0.0/tenant-status.sql` writes `Status` onto stored tenants; the API reads
  them correctly without it. `ApiContract.Version` and `ApiContract.DeliveryVersion` move to 8
  (#894).
