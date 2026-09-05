- **A `Request` action now carries the workflow run's idempotency key through to the connector.**
  `WorkflowRunner` has always put a stable key on every action's parameters, and `WebhookAction` has
  always sent it as `Idempotency-Key`, but `RequestAction` dropped it: neither it nor
  `RequestComposer` mentioned idempotency at all, so a retried call to a connector, the path an
  operator actually configures to reach a payment or accounting provider, carried no protection
  against being applied twice.

  The header name is a connector setting (`Settings["IdempotencyHeader"]`), not a request setting,
  because the spelling a provider wants is a property of the provider, and every request definition
  against the same connector should agree on it without repeating the choice. Unset defaults to
  `Idempotency-Key`. The literal value `off` switches it off, for a provider that rejects an unknown
  header; an empty setting falls back to the default rather than silently disabling the protection,
  so turning it off has to be spelled out.

  The key is sent unchanged, the same value `WebhookAction` sends, and goes through the same
  control-character check every templated header already passes. An action invoked outside the
  runner (a dry run, a test) has no key to send, and composes without the header rather than being
  refused.
