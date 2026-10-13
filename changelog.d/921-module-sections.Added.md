- **A module's documents could not travel with the rest of a tenant's setup, and a Portability
  bundle needed its own route.** The Forms module adds a `forms` section to the config document:
  each content type that takes public submissions, with the email field it verifies, checked the
  way `PUT /api/forms/{contentType}` checks it. The Portability module adds a `contents` section, so
  a bundle from `GET /api/portability/export` is accepted unchanged by `POST /api/config/plan` and
  `POST /api/config/apply`; entries are exported only when named, with
  `GET /api/config/export?sections=contentTypes,contents`, and applying them creates new entries
  each time, as the import always did. `GET /api/portability/export`, `POST /api/portability/import`
  and the bundle types are now obsolete, with removal planned for barakoCMS 6.0, and keep working
  until then (#921).
