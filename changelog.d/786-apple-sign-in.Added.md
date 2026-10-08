- **Sign in with Apple.** An OpenID Connect provider can now be set to `ResponseMode` `form_post`,
  which adds `POST /api/auth/oidc/{name}/callback` beside the GET with the same state, nonce and
  PKCE checks, and leaves its flow cookies SameSite=None so the provider's cross-site POST carries
  them. `SignedClientSecret` (`KeyId`, `TeamId`, a P-256 `PrivateKey` in PEM) makes the client
  secret an ES256 JWT, made for 30 days and made again when a day is left. `EmailVerifiedMayBeText`
  takes Apple's text form of `email_verified`. ExternalAuth 4.5.0 (#786).
