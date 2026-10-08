- **A reset socket or one 503 cost a workflow a whole durable attempt.** Outbound calls from the
  `Webhook` and `Request` actions, and the rest of the outbound HTTP client's calls, are now tried
  again inside the attempt when the failure is transient (a connection that did not open, 408, 429
  with its `Retry-After`, 502, 503, 504), with a timeout on each try and a breaker per tenant and
  host, using Carom. A call that may have reached the server is resent only when the receiver can
  recognise it (an idempotent method, an `Idempotency-Key`, or a webhook's delivery id). A 429 never
  counts toward a breaker, so one tenant's quota on a shared host does not refuse calls for the
  others. An open breaker fails the attempt as retryable and names only the host. Email is not
  retried inside the attempt; an optional `EmailSendTimeoutSeconds`, off by default, records a send
  that runs past it as unknown rather than retrying it. Settings are under `Workflows:Outbound`, and
  the host refuses to start when they let the slowest action take more than 80% of the shortest
  lease. The outbound client's own timeout is now the retry budget plus 30 seconds, 74 seconds by
  default, down from 100. This replaces the standard .NET resilience handler on that client, which
  also retried a 500 and a POST after a timeout, so `Microsoft.Extensions.Http.Resilience` is no
  longer a dependency. (#703)
