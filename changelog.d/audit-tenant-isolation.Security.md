- **Any tenant admin could read every tenant's audit log.** The audit trail is one global table, so
  the tenant-scoped session gave `GET /api/audit` no isolation and the `?tenant=` filter was
  caller-chosen. A tenant admin now sees only their own tenant's entries; reading across tenants is
  a SuperAdmin action.
