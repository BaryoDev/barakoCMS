- **The Caddyfile registered the literal string `${ACME_EMAIL}` as its Let's Encrypt contact.** Caddy
  reads a config-time placeholder as `{$VAR}`, which line 6 of the same file already used correctly.
  Compose does not template a bind-mounted file, and nothing in the tests or CI parses the Caddyfile,
  so every deployment following `deploy-in-production.md` had no real ACME contact address.
