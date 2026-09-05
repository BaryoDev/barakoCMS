- **The redirects resolve endpoint's output cache now actually caches.** It called
  `Options(x => x.CacheOutput(...))`, but nothing registered `AddOutputCache`/`UseOutputCache`, so
  the policy was metadata nobody read and every resolve hit Postgres. Output caching is registered
  now, placed after authentication and authorization so it never serves a response to a caller who
  should not see it, and the cache key is varied by tenant so one tenant's cached answer cannot be
  served to another.
