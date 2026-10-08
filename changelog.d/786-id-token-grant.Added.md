- **An id token grant for native apps.** `POST /api/auth/oidc/{name}/id-token` takes an id token a
  MAUI or mobile app got from the provider's SDK, checks it as the callback does (issuer, signature,
  expiry, `email_verified`) against the provider's `IdTokenAudiences` list, and answers with the
  same tokens or MFA challenge. A token addressed to the web client is taken only when its `azp`
  names a listed app, so a token from the browser sign-in cannot be exchanged. The request must
  carry the token's nonce. Each nonce is spent in the same commit as the sign-in, so a failed
  sign-in can be retried with the same token, and works once until the token expires. It shares the
  OIDC rate limit. An upgraded database needs `migrations/4.8.0/external-auth-used-nonces.sql`,
  which creates the empty `mt_doc_oidc_used_nonces` table and its `ExpiresAt` index;
  `migrations/4.8.0/rollback-external-auth-used-nonces.sql` drops it. ExternalAuth 4.5.0 (#786).
