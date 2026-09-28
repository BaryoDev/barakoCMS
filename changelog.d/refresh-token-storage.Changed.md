- **Refresh tokens were stored as issued, and a refresh answered with the new token in the body
  even for a browser that sent only the cookie.** The server now stores a SHA-256 hash of each
  refresh token and looks tokens up by it. Rows stored before the upgrade still refresh once and
  are replaced by a hashed row, so nobody is signed out and the last of them expires within seven
  days. Apply `migrations/4.5.0/refresh-token-hash-index.sql` on an existing database for the index
  on the hash. A refresh sent only the `barako_refresh` cookie now sets the new token in the cookie
  and returns an empty `refreshToken` in the body; a refresh sent the token in the body still
  returns it there, and sign-in is unchanged. `POST /api/auth/logout` accepts the refresh cookie
  when there is no usable bearer, revokes that user's refresh tokens and clears the cookie, so a
  console whose access token expired can still sign out. The cookie is now set for
  `/api/auth/logout` as well as `/api/auth/refresh`, and logout shares the auth rate limit.
