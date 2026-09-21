# Production Caddyfile and edge cache

The shipped production `Caddyfile` is a plain `reverse_proxy` with **no cache handler**.
Self-hosting with only that file therefore reaches Postgres on every public read.
Edge caching is expected from a CDN (or other shared cache) in front of the stack,
not from Caddy itself.

See [deploy-in-production.md](deploy-in-production.md#putting-a-shared-cache-or-cdn-in-front-of-it)
for when a shared cache is safe for multi-tenant deployments.
