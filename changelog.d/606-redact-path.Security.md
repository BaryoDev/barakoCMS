- **A webhook URL redacted for a run record or failure message kept its path, and that is where
  Discord, Slack and Teams put the secret.** `WebhookAction.Redact` kept scheme, host, port and
  path, dropping only userinfo and the query string, on the reasoning that those two are where
  most providers put a credential. Discord (`/api/webhooks/{id}/{token}`) and Slack
  (`/services/{a}/{b}/{secret}`) put theirs in the path instead, so a webhook that answered 500
  once wrote a replayable secret into a run record or a `WebhookDelivery`, readable by anyone
  holding `ViewWorkflowRuns`. Redaction now keeps only the scheme, host and port; the path is
  always dropped. Run and webhook-delivery records written from now on will show a shorter URL
  than before; that is the fix, not a regression.
