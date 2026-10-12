- **A tenant's setup lived in the database and nowhere else, so promoting it meant clicking it all
  again.** `GET /api/config/export` returns the caller's tenant's configuration as one JSON
  document, `POST /api/config/plan` says what applying a document would create, change or destroy
  without writing anything, and `POST /api/config/apply` converges the tenant to it in one
  transaction. A destructive change (a removed field, a deleted document, a cleared credential) is
  refused with 409 unless `allowDestructive` is sent. The document declares `"version": 1`, and an
  unknown top-level section is refused by name rather than skipped. The routes take the new
  `manage_config` capability, which Admin starts with, and every section is gated again on the
  capability its own endpoints ask for. The core sections are `contentTypes` and `roles` here, and
  the four sections listed under the next entry. Roles are shared by every tenant, so writing one takes `manage_roles` from a global
  role, and a role carrying a platform capability is written only by a SuperAdmin. A module adds a
  section by registering an `IConfigProvider`, a new public interface in
  `BarakoCMS.Abstractions` (#921).
