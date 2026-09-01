- **Logging out threw, and revocations were never cached.** `AddMemoryCache` sets a `SizeLimit`, and
  an entry stored without a `Size` raises `InvalidOperationException`. `TokenRevocationService` set
  both of its cache entries without one, so `POST /api/auth/logout` failed outright and every
  revocation check fell through to a database query on every authenticated request. There were no
  logout tests, which is why it survived. Found while building the session epoch, whose own cache
  write threw the same way and was invisible because the middleware catches and serves.
