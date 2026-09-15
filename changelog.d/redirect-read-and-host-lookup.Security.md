- **A redirect rule saved before paths were normalised could still send visitors off-site.** The
  save path cleans backslashes and control characters, but `GET /api/public/redirects/resolve`
  returned the stored `toPath` as it was, so an older rule holding `/\evil.com` answered with it,
  cached for five minutes. The resolve endpoint now normalises both paths when it serves them.
- **`GET /api/tenants/by-host/{host}` answered 404 for a tenant routed by subdomain.** It read only
  the registered domain map, while request routing also falls back to the leading subdomain. It now
  resolves the host the same way and answers the handle when that tenant exists and is active.
