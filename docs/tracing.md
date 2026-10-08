# Tracing and the correlation id

Two things, and only the second needs configuring.

Every request has one correlation id. It is on the response, on the request's log lines and on
every event the request stores, with nothing configured.

With an OTLP endpoint configured, barakoCMS also exports OpenTelemetry spans: the request, each
outbound HTTP call and its tries, each database command made under one of those, each workflow
action, each job the queue runs, and each tick of the scheduler and of collection syncs. A caller
that sent `traceparent` sees this API as part of its own trace.

## The correlation id

`CorrelationIdMiddleware` picks it, in this order:

1. The caller's `X-Correlation-ID`, when it is 1 to 64 characters of letters, digits, `.`, `_` and
   `-`. A value that is longer, or holds anything else, is ignored and never echoed.
2. The trace id of the request. A caller that sent a valid `traceparent` and no `X-Correlation-ID`
   gets its own trace id back.
3. A fresh 32 character id.

The response carries it in `X-Correlation-ID`, and every log line of the request has it as
`CorrelationId`.

Before 4.6 the header was echoed as sent, whatever it held. A client that sends an id outside the
rule above now gets a different one back.

### What is stored

Each event in the event store has two metadata columns, filled when the event is appended:

| Column | Holds |
|---|---|
| `correlation_id` | The correlation id of the request that wrote the event. |
| `causation_id` | The W3C `traceparent` of the span that wrote it, `00-<trace id>-<span id>-<flags>`. |

Correlation says which request something belongs to. Causation says which step wrote it. A request
that publishes an entry, and a workflow action it fires that creates a second entry, leave two
events with the same correlation id. With tracing on, the first names the request's span as its
cause and the second names the action's span. With tracing off there is no action span, and the
second names the request's span too.

```sql
select seq_id, type, correlation_id, causation_id
from mt_events
where correlation_id = '4bf92f3577b34da6a3ce929d0e0e4736'
order by seq_id;
```

A workflow run copies both from the event that triggered it. `GET /api/workflow-runs/{id}` returns
the run's `correlationId`, and events written by the run's actions carry it too.

Both are null where no request caused the write: a scheduled publish, a collection sync, a module's
own background work, and every event and run stored before 4.6. Nothing reads either as required.

An existing database needs `migrations/4.6.0/event-correlation-metadata.sql` before 4.6 starts.
It can be applied while the old build is still serving; see
[upgrading-to-4.0.md](upgrading-to-4.0.md).

The two values are put on the events when a session saves, by a listener on the document store, so
a module that opens its own session from `IDocumentStore` gets the same ids as the scoped one.
Marten's default for a session is the raw parent id of the current span, which is the caller's
header as sent; that value is never stored.

## Exporting spans

Off by default. With `Tracing:Otlp:Endpoint` unset, no tracer is registered, nothing listens for
spans and no connection is opened.

| Setting | Default | Meaning |
|---|---|---|
| `Tracing:Otlp:Endpoint` | unset | The collector's OTLP endpoint. Setting it turns tracing on. |
| `Tracing:Otlp:Protocol` | `grpc` | `grpc` or `http/protobuf`. |
| `Tracing:Otlp:Headers` | unset | Headers for every export, `name=value,name=value`. A secret. |
| `Tracing:Otlp:TimeoutSeconds` | `10` | How long one export may take. 1 to 60. |
| `Tracing:ServiceName` | `barakocms` | The `service.name` the spans are reported under. |
| `Tracing:SampleRatio` | `1.0` | The share of traces started here that are recorded. 0 to 1. |
| `Tracing:MaxQueueSize` | `2048` | Finished spans that may wait for export. 1 to 65536. |

As environment variables:

```bash
Tracing__Otlp__Endpoint=http://otel-collector:4317
Tracing__ServiceName=cms-production
```

With `http/protobuf` give the full URL, path included, such as
`http://otel-collector:4318/v1/traces`. The exporter does not add the path to an endpoint set this
way.

A value out of range, an unknown protocol or an endpoint that is not an absolute http or https URL
stops the host at startup. The settings are read once, at startup.

### When the collector is down

Export never runs on a request. A finished span is put on a queue and a background thread sends
batches of up to 512. If the collector does not answer within `Tracing:Otlp:TimeoutSeconds`, that
batch is dropped. If the queue already holds `Tracing:MaxQueueSize` spans, a new span is dropped.
Requests are not slowed and nothing is retried from disk, so spans from an outage are lost.

### Sampling

A request that arrives with a sampled `traceparent` is recorded, and one that arrives with an
unsampled `traceparent` is not, whatever `Tracing:SampleRatio` says. The ratio decides only for
traces that start here.

### What a span carries

The request span, started by ASP.NET Core:

`http.request.method`, `http.route`, `http.response.status_code`, `url.scheme`,
`network.protocol.version`, `error.type`, `barako.correlation_id`.

The span of an outbound HTTP call (a webhook, a connector request, an email provider):

`http.request.method`, `http.request.resend_count`, `http.response.status_code`,
`network.protocol.version`, `server.address`, `server.port`, `error.type`.

`workflow.action`, one per attempt of a workflow action, from the source named `BarakoCMS`:

`barako.tenant`, `barako.workflow.id`, `barako.workflow.run_id`, `barako.workflow.trigger`,
`barako.workflow.action`, `barako.workflow.action_ordinal`, `barako.workflow.attempt`,
`barako.workflow.outcome`, `barako.correlation_id`.

The span of a database command, started by Npgsql:

`db.system`, `db.name`, `db.operation`, `db.statement`, `net.transport`, `net.peer.name`,
`net.peer.port`.

The statement is the command text with `$1` or `@name` where a value goes, cut to 2,000
characters. Parameter values are sent to Postgres apart from it and are never on a span. The
connection string, the database user and the connection id are removed.

The rest come from the source named `BarakoCMS`. Every one carries ids, counts, fixed words and
names an administrator or the code chose, and nothing else:

| Span | Attributes |
|---|---|
| `job.claim`, a queue poll that found due jobs | `barako.job.queue`, `barako.job.claimed` |
| `job.run`, one job from its claim to its outcome | `barako.tenant`, `barako.job.id`, `barako.job.command`, `barako.job.attempt`, `barako.job.outcome` |
| `job.finish`, the write that records the outcome | `barako.tenant`, `barako.job.id` |
| `scheduler.sweep`, one tick of scheduled publishing | `barako.sweep.held` |
| `scheduler.tenant`, that tick in one tenant | `barako.tenant`, `barako.scheduler.transitions` |
| `collection_sync.sweep`, one tick of collection syncs | `barako.sweep.held` |
| `collection_sync.run`, one sync | `barako.tenant`, `barako.collection_sync.id`, `barako.collection_sync.outcome`, and the `created`, `updated`, `unchanged` and `skipped` counts under the same prefix |
| `outbound.call`, one call through the retry and the breaker | `barako.outbound.scope`, `barako.outbound.destination` (the host), `barako.tenant`, `barako.outbound.tries`, `barako.outbound.outcome` |
| `outbound.try`, one try of that call | `barako.outbound.try`, `barako.outbound.outcome` |

`barako.job.outcome` is `succeeded`, `retried` or `dead_lettered`, or `gone` (the record was
deleted), `changed` (another node moved it), `error` (the write failed), `abandoned` (no outcome
within twice the lease) or `reclaimed` (its lease ran out and it was claimed again).
`barako.sweep.held` says whether this instance held the sweep's lock; one that did not did
nothing else. `barako.outbound.outcome` is `ok`, `failed`, `timeout` or `breaker_open`.

Each `outbound.call` has a `retry` event for every retry Carom decided to make, with
`barako.outbound.try` (the try about to run), `barako.outbound.delay_ms` (the wait before it) and
`exception.type` (the type of what failed, or `result`). The HTTP span of each try is a child of
its `outbound.try`, so a call that took three tries shows three of each.

A job's span never holds the command, which is what the request queued, and never the error a
failure stored. A queue poll that found nothing starts no span.

That is the whole list. `SpanScrubber` removes every other attribute from the request, outbound
call and database spans before export, along with the status description. The request path and query string are not exported,
because a path here can hold a share link or a preview token. The host a request was sent to is
not exported either: it is the caller's Host header. The URL of an outbound call is not exported,
only its host and port, because a webhook URL is often the credential. Exceptions are not recorded
on spans. No header, body, token, email address or action parameter is on any span.

The lists apply by span kind as well as by source: every Server span is cut to the request list
and every Client span other than Npgsql's to the outbound call list, whatever started it. So a
host that gives ASP.NET Core a source of its own does not get the path back, and a Client span
from a source you add yourself is cut to the outbound call list too.

Two kinds of span are not exported at all:

- A database command with no parent span. That is the projection daemon and the other background
  polls, which would be most of what a collector receives and say little.
- A cut-down span holding an event with attributes. Npgsql records a failed command as an event
  carrying the server's message, which can quote the values a row held, and an event cannot be
  edited once added. The span is dropped; its parent, a request or a job, still shows the error.

Spans from the `Carom` source are kept with their name and timing only. Carom 2.0.1 starts none
itself, so that matters only for a module that starts its own through Carom's telemetry package.

The health probes under `/health` and the `/metrics` scrape are not traced.

### Workflow actions

The workflow runner has no request. Each action's span is started under the `traceparent` stored
on its run, which is the request span that wrote the triggering event, so in a trace viewer the
action sits under the request that caused it, however much later it ran. An outbound call the
action makes is a child of that span, and the receiver is sent the same trace in `traceparent`.

A run with no stored `traceparent` starts a trace of its own.

### Not covered

- A job handler's own work. The queue runs the handler on its own worker, outside anything this
  code starts, so an outbound call a handler makes is exported as a trace with no parent rather
  than under `job.run`.
- Marten's own spans. Its source is not listened to; the Npgsql span of each command is.
- Metrics and logs are not exported over OTLP. Metrics stay on `/metrics`.

### In your own host

A host that calls `AddBarakoCMS` and runs its own OpenTelemetry setup gets the workflow spans by
adding the source:

```csharp
builder.Services.AddOpenTelemetry().WithTracing(t => t.AddSource("BarakoCMS"));
```

Leave `Tracing:Otlp:Endpoint` unset in that case. `SpanScrubber` is only registered with the
built-in exporter, so the request, outbound call and database spans your own setup exports carry
what the instrumentation puts on them. Do not add the `Npgsql` source to your own setup without a
processor of your own that removes what is listed above.

### Carom

The retries are Carom's. barakoCMS subscribes to Carom's retry hook (`CaromHooks.OnRetry`, in the
Carom package it already uses) and writes the `retry` events itself. It does not reference
`Carom.Telemetry.OpenTelemetry`: that package's floor of OpenTelemetry.Api 1.15.3 resolves fine
against the 1.19 packages here, but in 2.0.1 it starts no spans, its `Subscribe` feeds meters
only, which are not exported here, and those meters tag each breaker by its key, which holds the
tenant and host. Subscribing to the hook gives the same signal with no new dependency.
