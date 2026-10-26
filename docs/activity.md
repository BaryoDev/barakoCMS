# Activity feed

`GET /api/activity` is one feed of what happened in the tenant, merged from where each thing already
lives: the audit log, workflow runs, the content event store and an email module's bounces. Nothing
new is stored.

## Request

| Query | Meaning |
| --- | --- |
| `since` | A cursor from an earlier response, or a timestamp. Left out, the newest rows come first. Given, the rows after it come oldest first. |
| `kinds` | Comma separated kinds to keep. Left out, every kind. A name this version does not know is ignored. |
| `pageSize` | 1 to 100, default 20. `page` is ignored, the cursor is what moves. |

An unreadable `since` is a 400.

## Polling

Read once without `since`, then pass the `cursor` of each response as `since` of the next. The cursor
lags up to five seconds behind now, so a row whose transaction commits late is still seen on the next
poll. A row can therefore arrive twice. Replace by `id`. `hasMore` is true when more rows were already
waiting, and the next poll should not wait. A page can hold fewer rows than `pageSize` while
`hasMore` is true, because a row the caller may not read is left out after the page is cut.

## Row

```json
{
  "id": "run-3f2a...",
  "at": "2026-10-26T09:14:02.113+00:00",
  "kind": "run.failed",
  "severity": "error",
  "actor": { "kind": "system", "name": "Workflow" },
  "subject": { "kind": "run", "id": "3f2a...", "title": "Notify editors" },
  "text": "A workflow run failed.",
  "ref": "/workflows/runs/3f2a..."
}
```

`text` is a fixed sentence per kind. It never holds the subject's title, so a client sets
`subject.title` itself, escaped. `ref` is a console path. `severity` is `info`, `warning` or `error`.
A client renders a kind it does not know by its name.

A workflow run keeps its `id` as it changes, so a run that failed and was retried to success replaces
its own row and changes kind.

## Kinds

| Kind | Severity | Source | Needs |
| --- | --- | --- | --- |
| `published` | info | content events: an entry created as published, or moved to published (the scheduler's moves show `System` as the actor) | read permission on the entry |
| `scheduled` | info | content events: a publish time set | read permission on the entry |
| `edited` | info | content events: an entry's data updated | read permission on the entry |
| `run.succeeded` | info | workflow runs that succeeded | `view_workflow_runs` |
| `run.failed` | error | workflow runs that failed or partly failed | `view_workflow_runs` |
| `bounced` | warning | `IMailBounceSource.ListAsync` of an email module | `manage_email_settings` and an email module |
| `signin` | info | audit rows `auth.login.succeeded` | `view_audit_log` |
| `imported` | info | audit rows `redirect.imported` and `tenant.imported` | `view_audit_log` |
| `type.changed` | info | audit rows `contenttype.*` | `view_audit_log` |

The route needs `view_audit_log`, and a caller without it gets 403. Past that, a kind the caller lacks
the second capability for is left out, not an error. Every read is for the caller's tenant, a
SuperAdmin's too. An entry hidden from the caller by its sensitivity, its branch or the role's
read rules gives no row.

No event type name, stream id or event body is in a response.

## Filter chips

The console's chips map to `kinds` like this. `type.changed` shows under Everything only.

| Chip | `kinds` |
| --- | --- |
| Everything | left out |
| Problems | `run.failed,bounced` |
| Publishing | `published,scheduled,edited` |
| Workflow runs | `run.succeeded,run.failed` |
| Sign-ins | `signin` |
| Imports | `imported` |

## Bounces from an email module

`IMailBounceSource` gained `ListAsync` (oldest first after a position) and `ListNewestAsync`, both
with a default that returns nothing, so an existing implementation still compiles and the feed shows
no bounces for it. `ListAsync` orders by time and then by id, and with no id the position is a time and
the bounces are those strictly later. A bounce's title is a short label for the message, never the
recipient address.
