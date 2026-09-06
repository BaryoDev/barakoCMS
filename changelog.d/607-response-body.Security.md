- **A webhook delivery's response body needed only `view_workflow_runs`, the same capability that
  reads every workflow run.** Two other places in this codebase refuse to carry a response body at
  all, because a 401 from an OAuth provider frequently echoes the credential that was sent; the
  delivery log was the one place that reasoning had not reached. `GET /api/webhook-deliveries` now
  needs a second capability, `view_webhook_response_bodies`, to read the `responseBody` field.
  Nothing else on the row is gated further: a caller holding only `view_workflow_runs` still sees
  every delivery, its status, its error and everything else, with `responseBody: null`.
  `docs/access-control.md` covers the split.
  **A holder of `view_workflow_runs` who is not also granted `view_webhook_response_bodies` loses
  the ability to read a delivery's response body on upgrade.** Admin's defaults do not include the
  new capability, since Admin never held this access before the split; only SuperAdmin (via `*`)
  and a role an operator grants it to explicitly can read a body. Grant `view_webhook_response_bodies`
  to whichever role should keep debugging webhooks.
- **The response body now expires on its own.** `Webhooks:ResponseBodyRetentionHours` (default 24)
  clears `responseBody` on rows older than the window, on the same hourly sweep that already prunes
  the delivery log at `Webhooks:DeliveryLogRetentionDays` (default 30, unchanged). The row survives;
  only the body is cleared, and `responseBodyClearedAt` is stamped so a cleared body reads
  differently from one that was empty to begin with (nothing answered, or the body has not expired
  yet). `docs/webhooks.md` covers both windows.
