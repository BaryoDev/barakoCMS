- **Workflow execution logs stored before redaction kept raw exception messages and parameter values
  in the database, and a retried failure with no recorded kind was audited as transient.** Old logs
  were redacted only when served, so the rows themselves still held a recipient, a URL or a provider
  error body, and nothing removes them. A startup pass now rewrites every unredacted log through the
  same redaction and marks it `redacted`, on every partition, and is safe to run again. Retrying a
  failed attempt whose `retryable` is null now records `wasPermanent` as `"unknown"` in the
  `workflow.action.retried` audit entry instead of `false`.
