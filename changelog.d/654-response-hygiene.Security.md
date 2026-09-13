- **Token and key responses could be kept by a browser or proxy cache, and a missing base URL was
  described to anonymous callers.** Responses under `/api/auth`, `/api/me`, `/api/api-keys` and
  `/api/preview` now send `Cache-Control: no-store` and `Pragma: no-cache`, so a login, refresh,
  profile, new API key or preview token is not stored (#654). Kestrel no longer sends
  `Server: Kestrel`. With no `App:BaseUrl` or `Feeds:SiteUrl`, the sitemap and feed still answer 503
  but no longer name the settings in the body, and the OAuth start and callback routes answer the
  same 503 instead of a 500 whose body named `App:BaseUrl`. The setting to fix it is in the API log.
  The header changes are additive.
