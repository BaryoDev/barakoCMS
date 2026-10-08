- **Background work and database commands now show up in traces.** With `Tracing:Otlp:Endpoint`
  set, the job queue exports a span for each claim, each run and the write that records its
  outcome, tagged with the tenant and never the command. Each tick of scheduled publishing and of
  collection syncs is a span, with one per tenant or per sync under it. Each outbound call through
  the retry and the breaker is a span with one child per try and a `retry` event from Carom's
  retry hook for each retry. Npgsql's span of each database command made under one of these
  spans or a request is exported cut down to the statement and the server it went to: no
  parameter values, no connection string, no database user. A failed command, whose event quotes
  the server's message, is not exported, and neither is a command with no parent, which includes
  the background polls and a job handler's own commands. Each dropped span is counted on
  `barako.tracing.spans_dropped`, by reason. See `docs/tracing.md` (#691).
