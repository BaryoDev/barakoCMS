- **The private site check is cached per tenant.** An anonymous `/api/public/*` request in a tenant
  that is not in Private mode no longer reads the content table to find out. The answer is cleared
  on the node in the same commit that writes, deletes or publishes a `site` entry, by any path
  (endpoints, config apply, seed files), so a tenant switched to Private refuses the next request.
  Another node learns within `Delivery:PrivateSite:CacheSeconds` (default 5, `0` reads every
  request). No route, field or status code changes, and neither contract number moves.
