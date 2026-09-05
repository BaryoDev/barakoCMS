- **Public delivery responses now carry `Vary: X-Tenant`.** `TenantResolutionMiddleware` resolves
  the tenant from the `X-Tenant` header before it looks at Host, and the response is built entirely
  from that tenant's content, but `Cache-Control: public, max-age=60` went out with no `Vary`. A
  shared cache keyed on the URL alone could serve one tenant's response to another, on any
  deployment where more than one tenant is reachable through the same hostname and path (header- or
  path-routed multi-tenancy; hostname-per-tenant was already safe, since Host is part of the URL).
  `PublicDelivery.SetCache` sets `Vary` now, which covers the list, search, slug, feed and sitemap
  routes in one place. `Vary` is necessary but not sufficient: `docs/deploy-in-production.md` now
  says which deployment shapes are safe to put a shared cache or CDN in front of, and what the CDN
  itself has to be configured to do on the ones that are not.
