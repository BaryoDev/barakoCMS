- **Switching tenant no longer issues a second session.** `POST /api/me/switch` used to store and
  return a new seven day refresh token alongside the access token. It now returns the access token
  for the target tenant only; `refreshToken` stays in the response but is empty and
  `refreshTokenExpiry` is the default value. The refresh token from sign-in, in the body or the
  refresh cookie, already covers every tenant the user belongs to: a refresh mints for the
  `X-Tenant` it is sent and re-checks membership. A client that stored `refreshToken` from the switch
  response has to keep its existing one when the value is empty. This moves `X-Api-Contract-Version`
  to 5.
