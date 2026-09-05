- **`ConditionalAction` no longer reports success when one of its child actions fails.** Each child
  ran inline and its result was logged as a warning and dropped, so a conditional whose branch
  failed to send anything still reported `Success()`. The run record said the workflow did
  something it did not do.

  A failing child now feeds into the conditional's own result. If nothing in the branch has
  succeeded yet, the failure is retryable, since retrying only re-runs children that never had an
  effect. The moment one child has succeeded alongside a failing one, the conditional reports a
  non-retryable failure instead: children still run with no attempt record and no idempotency key
  of their own (that reshape is 4.1), so a retry re-runs every child from the top, and offering one
  here would resend whatever the earlier child already sent. The aggregated error names which
  child action types failed, never the child's own error text, which can carry what it was sending.
