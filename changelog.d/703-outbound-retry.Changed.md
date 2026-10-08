- **A reset socket or one 503 cost a workflow a whole durable attempt.** Outbound calls from the
  `Webhook`, `Request` and `Email` actions are now tried again inside the attempt when the failure is
  transient (a connection that did not open, 408, 429 with its `Retry-After`, 502, 503, 504), with a
  timeout on each try and a breaker per destination host, using Carom. A call that may have reached
  the server is resent only when the receiver can recognise it (an idempotent method, an
  `Idempotency-Key`, or a webhook's delivery id), and an email that timed out is never resent. An
  open breaker fails the attempt as retryable and names only the host. Settings are under
  `Workflows:Outbound`, and the host refuses to start when they let one call take more than half the
  shortest lease. This replaces the standard .NET resilience handler on the outbound client, which
  also retried a 500 and a POST after a timeout, so `Microsoft.Extensions.Http.Resilience` is no
  longer a dependency. (#703)
