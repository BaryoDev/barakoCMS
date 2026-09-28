- **Switching tenant no longer issues a second session or renews the current one.**
  `POST /api/me/switch` used to store and return a new seven day refresh token, and every call,
  including one to the tenant the caller was already in, returned a fresh 15 minute access token.
  It now returns an access token for the target tenant only, that token expires when the presented
  one would have, and the presented token is revoked. `refreshToken` stays in the response but is
  empty and `refreshTokenExpiry` is the default value. The refresh token from sign-in, in the body
  or the refresh cookie, already covers every tenant the user belongs to: a refresh mints for the
  `X-Tenant` it is sent and re-checks membership. A client that stored `refreshToken` from the
  switch response has to keep its existing one when the value is empty, and has to use the returned
  token from then on. `ITokenIssuer` gains an overload taking a `notAfter` cap, with a default
  implementation that refuses a token it cannot cap. This moves `X-Api-Contract-Version` to 5.
