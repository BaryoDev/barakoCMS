- **An id token grant for native apps.** `POST /api/auth/oidc/{name}/id-token` takes an id token a
  MAUI or mobile app got from the provider's SDK, checks it as the callback does (issuer, signature,
  expiry, `email_verified`) against the provider's `IdTokenAudiences` list, and answers with the
  same tokens or MFA challenge. The request must carry the token's nonce, and each nonce works once
  until the token expires. It shares the OIDC rate limit. An upgraded database needs
  `migrations/4.8.0/external-auth-used-nonces.sql`, which creates the empty `mt_doc_oidc_used_nonces`
  table; `migrations/4.8.0/rollback-external-auth-used-nonces.sql` drops it. ExternalAuth 4.5.0 (#786).
