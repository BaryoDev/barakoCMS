- **barakoCMS is the API only.** The console under `admin/` now lives at
  [BaryoDev/barakoBrew](https://github.com/BaryoDev/barakoBrew) and still publishes
  `ghcr.io/baryodev/barako-admin`; the marketing site under `site/` has its own repository. Gone
  with them: the Admin UI, Site and "Admin against the real API" CI jobs, the admin image in the
  release and its SBOM, the admin-only playground deploy, the admin and site Dependabot entries,
  the admin service in every compose file and the quickstart, the `DOMAIN_ADMIN` Caddy route,
  `scripts/smoke-check.sh` and `assets/admin`. The API's own surface is Swagger, and the quickstart
  now passes `SWAGGER_ENABLED` through. Nothing in the packages or the API changed (#505).
