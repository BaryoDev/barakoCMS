<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.Diagnostics logo" />
  <h1>BarakoCMS.Diagnostics</h1>
  <p><em>Collect browser errors from your frontend so you can find them later.</em></p>
</div>

---

Your app POSTs captured browser errors — message, stack, page, user — and this module deduplicates
them by fingerprint with an occurrence count, so a bug hit 4,000 times is one row that says 4,000
rather than 4,000 rows.

## Enable it

```sh
dotnet add package BarakoCMS.Diagnostics
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=Diagnostics`). Unset, every referenced module runs and the API logs
one warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.Diagnostics.DiagnosticsModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.


## Endpoints

| Method & path | Purpose | Access |
|---|---|---|
| `POST /api/client-errors` | Report a batch of captured errors | Anonymous |
| `GET  /api/client-errors` | Browse what has been reported for the current tenant (every tenant for a caller holding the role globally) | `Admin` / `SuperAdmin` |
| `GET  /api/incidents` | Every failure source, grouped and redacted (see below) | `manage_client_errors` |
| `POST /api/client-errors/{id}/resolve` | Mark a fingerprint resolved, with an optional `reference` (a ticket or pull request link or number, up to 500 characters) and `note` (up to 2,000); `{ "resolved": false }` reopens it and clears both | `Admin` / `SuperAdmin` |

An error is kept per tenant: the report's `tenant`, or the tenant the request resolved to when it
names none. The same fault in two tenants is two rows, and resolving another tenant's error answers
404 unless the caller holds the role globally. Tenants are stored lowercased.

Reporting is anonymous on purpose: the errors worth catching often happen before anyone signs in.
That makes it a spammable endpoint, so it runs under its own tighter `telemetry` rate-limit policy
rather than the global budget.

## Incidents

`GET /api/incidents` is one paged list across every place failures are recorded, grouped by
fingerprint, so a person looking for "what is broken" reads one list instead of four:

| Source | What it reads | Also needs |
|---|---|---|
| `client-errors` | Browser errors reported to `POST /api/client-errors` | |
| `dead-letters` | Background jobs that used every attempt, dated by when they gave up | `view_jobs` |
| `failed-runs` | Workflow actions that failed for good | `view_workflow_runs` |
| `failed-deliveries` | Webhook and connector sends with no answer or a non-2xx one | `view_workflow_runs` |

The view needs `manage_client_errors`. A source whose records the caller could not read directly is
left out for that caller. Each group carries `source`, `fingerprint`, `title`, `detail`, `count`,
`firstSeenAt`, `lastSeenAt`, `tenants`, `releases` and the resolution fields. Query with
`source`, `resolved`, `days` (1 to 90, default 30), `page` and `pageSize`. Each source contributes at
most its newest 1,000 occurrences in the window. Client errors span every tenant for a caller who
holds the capability through a global role; the other sources are stored per tenant and read for
the current one. A response body from a delivery is never read here.

Another module adds a source by registering a scoped `barakoCMS.Core.Interfaces.IIncidentSource`.

## Redaction

Titles, details and resolution text in the incidents view, and everything the forwarder sends, go
through one set of rules on the server:

- JSON web tokens, and the value after `Bearer` or `Basic`
- every query string value, and the user and password in a URL
- the value after a key named like a credential: password, secret, token, api key, access key,
  private key, auth, session, cookie, signature, otp
- the value after the name of a content field marked Sensitive or Hidden, written as
  `Name: value`, `Name=value`, `"Name": "value"` or escaped as `\"Name\":\"value\"`. A quoted
  value goes whole, escapes included; an unquoted one goes up to the next comma, semicolon,
  ampersand, bracket or line end, spaces and all
- email addresses, and any run of 32 or more letters, digits, `-` or `_` holding both a letter and
  a digit, which is what an API key or session id looks like

The dead-letter, failed-run and failed-delivery sources also cut every URL in their text to its
scheme and host, as `/api/jobs` and the workflow-run list do, because a webhook URL can carry its
secret in the path. Browser errors keep their paths, since the script or page is what a reader
needs, with every query value removed.

These are patterns. A Sensitive value that appears with no field name in front of it is not
recognisable as one and stays. The stored error and `GET /api/client-errors` are unchanged: they
are the unredacted record, behind the same capability.

## Forwarding to an error tracker

Off by default. Set a DSN and captured errors are also sent to any receiver that speaks Sentry's
envelope protocol: Sentry, or GlitchTip, which is MIT licensed and self-hostable.

```sh
Modules__Diagnostics__Forwarding__Enabled=true
Modules__Diagnostics__Forwarding__Dsn=https://PUBLIC_KEY@errors.example.com/PROJECT_ID
Modules__Diagnostics__Forwarding__Environment=production   # optional
Modules__Diagnostics__Forwarding__QueueCapacity=500        # optional
Modules__Diagnostics__Forwarding__TimeoutSeconds=5         # optional
```

Local storage and `GET /api/client-errors` are unchanged; this is a copy, not a replacement.
Forwarding never touches the ingest response: reports wait in a queue of at most `QueueCapacity`,
a full queue drops new ones, and a receiver that is down, slow or answering errors is logged and
skipped with no retry.

**Exactly what leaves the process**, per report, as one envelope event:

| Field | From |
|---|---|
| `message.formatted` | the error message, redacted |
| `culprit` | the reported source (script, component or API path), redacted |
| `request.url` | the page URL, redacted (query values removed) |
| `level` | `error` or `warning` |
| `release` | the reported app version |
| `environment` | the configured environment, when set |
| `fingerprint` | this module's fingerprint of the fault |
| `tags.kind`, `tags.tenant`, `tags.status` | the report's kind, tenant slug and HTTP status |
| `event_id`, `timestamp`, `platform`, `logger` | generated |

The envelope header also carries the DSN, as the protocol requires. **Not sent:** the stack, the
user agent, the user id or username, and the client's address.

## Sending errors

The barakoCMS admin ships a reporter you can copy. Two rules matter if you write your own: send with
plain `fetch` rather than an HTTP client that retries or refreshes tokens (a reporter that can fail
by reporting is a loop), and cap sends per page session so a render loop cannot flood the API.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under MPL-2.0.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
