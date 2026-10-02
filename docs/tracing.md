# Tracing and the correlation id

Two things, and only the second needs configuring.

Every request has one correlation id. It is on the response, on the request's log lines and on
every event the request stores, with nothing configured.

With an OTLP endpoint configured, barakoCMS also exports OpenTelemetry spans: the request, each
outbound HTTP call, and each workflow action. A caller that sent `traceparent` sees this API as
part of its own trace.

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
See [upgrading-to-4.0.md](upgrading-to-4.0.md): it has to run with every instance of the old build
stopped.

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
`network.protocol.version`, `server.address`, `server.port`, `error.type`, `barako.correlation_id`.

The span of an outbound HTTP call (a webhook, a connector request, an email provider):

`http.request.method`, `http.request.resend_count`, `http.response.status_code`,
`network.protocol.version`, `server.address`, `server.port`, `error.type`.

`workflow.action`, one per attempt of a workflow action, from the source named `BarakoCMS`:

`barako.tenant`, `barako.workflow.id`, `barako.workflow.run_id`, `barako.workflow.trigger`,
`barako.workflow.action`, `barako.workflow.action_ordinal`, `barako.workflow.attempt`,
`barako.workflow.outcome`, `barako.correlation_id`.

That is the whole list. `SpanScrubber` removes every other attribute from the first two before
export, along with the status description. The request path and query string are not exported,
because a path here can hold a share link or a preview token. The URL of an outbound call is not
exported, only its host and port, because a webhook URL is often the credential. Exceptions are not
recorded on spans. No header, body, token, email address or action parameter is on any span.

The health probes under `/health` and the `/metrics` scrape are not traced.

### Workflow actions

The workflow runner has no request. Each action's span is started under the `traceparent` stored
on its run, which is the request span that wrote the triggering event, so in a trace viewer the
action sits under the request that caused it, however much later it ran. An outbound call the
action makes is a child of that span, and the receiver is sent the same trace in `traceparent`.

A run with no stored `traceparent` starts a trace of its own.

### Not covered

- Database spans. Npgsql's spans carry the statement text, the database user and the connection
  string, so they are left out until they can be cut down the same way.
- The job queue, the scheduler and collection syncs start no spans of their own. An outbound call
  they make is exported as a trace with no parent.
- Metrics and logs are not exported over OTLP. Metrics stay on `/metrics`.

### In your own host

A host that calls `AddBarakoCMS` and runs its own OpenTelemetry setup gets the workflow spans by
adding the source:

```csharp
builder.Services.AddOpenTelemetry().WithTracing(t => t.AddSource("BarakoCMS"));
```

Leave `Tracing:Otlp:Endpoint` unset in that case. `SpanScrubber` is only registered with the
built-in exporter, so the request and outbound call spans your own setup exports carry what the
instrumentation puts on them.
