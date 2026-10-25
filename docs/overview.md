# Overview

`GET /api/overview` is the rule-based "Today at a glance": counts with a prior period, the items that
need a person, and the health strip. It exists so every client reads one set of facts. There is no AI
here. The rules are in `barakoCMS/Features/Overview/OverviewRules.cs`, one function each.

Any signed-in caller may ask, and so may a `monitor:read` key (it acts as its owner, so the owner's
capabilities decide the parts). A part the caller cannot read is `null`, never zero. Everything is
for the caller's tenant, and every number is a count or a capped list.

## Response

| Field | Needs | Meaning |
| --- | --- | --- |
| `verdict` | | `ok`, `warn` or `bad`: the worst status among the parts the caller sees |
| `headline` | | `All systems normal`, or `1 warning` (`1 problem` when any is bad) |
| `generatedAt` | | when it was read |
| `counts.publishesDue` | content read | scheduled entries due in the next 24 hours, overdue ones included. `prior` is null, since a publish clears its time when it runs |
| `counts.failedRuns` | `view_workflow_runs` | failed and partially failed runs in the last 24 hours, and the 24 before |
| `counts.bounces` | `manage_email_settings` and an email module | bounces in the last 24 hours, and the 24 before |
| `counts.signIns` | `view_audit_log` | successful sign-ins to the tenant, last 24 hours and the 24 before |
| `workflows` | `view_workflow_runs` | `runs24h`, `successRate`, `verdict` and `summary`, the same facts as `GET /api/workflows/health` |
| `health` | `view_monitoring` | `verdict` and `items`: API (with `version`, left out for an API key), Database (with `ms`), Storage, Runner, and Mail (only with `manage_email_settings` as well) |
| `needsYou` | | items the caller may see, worst first then by id: `id`, `severity`, `text`, `ref`, `resolvable` |

A count is `{ current, prior }`, so a client can write "+12% vs prior" or "3 fewer". `ref` is
`{ kind, id }`. The kinds are `mailBounces`, `workflowRuns`, `content` and `runner`.

Storage is the disk space health check. Mail is left out of `health.items` when no email module
reports bounces, and `counts.bounces` is null: with no data it is not called ok.

## Rules

| Function | Result |
| --- | --- |
| `Api` | always ok, carries the version |
| `Database` | the Database health check, and warn from 500 ms, bad from 2000 ms |
| `Storage` | the Disk Space health check |
| `Runner` | the Workflow Projection health check. Not ok also adds the `runner.stalled` item |
| `Mail` | warn while any bounce is unresolved, otherwise ok ("Delivering.") |
| `Bounces` | item `mail.bounces` (warn, resolvable) while any bounce is unresolved |
| `FailedRuns` | item `workflows.failing` when the 24 hour workflow verdict is not ok, with that verdict as severity |
| `OverduePublishes` | item `content.overdue` (warn) for entries still scheduled 5 minutes past their time |
| `Verdict` | the worst of the statuses |
| `Headline` | the count of rules that are not ok |

The workflow verdict is `WorkflowHealthRules`, shared with `GET /api/workflows/health`: ok at 90
percent and up, warn at 70, otherwise bad, and ok when nothing has finished.

## Resolving

`POST /api/overview/items/{id}/resolve` (needs `manage_email_settings`) clears a resolvable item.
For `mail.bounces` it marks every unresolved bounce of the tenant resolved, including any that arrived after the page loaded (it clears what is unresolved at the time of the click), so the next read shows
Mail delivering, no item, and a verdict that no longer counts it. An unknown or non-resolvable id,
or a deployment with no bounce source, answers 404. A key cannot call it.

## Bounce sources

Bounce data comes from `IMailBounceSource` (in `BarakoCMS.Abstractions`), which an email module
registers. The core stores nothing. Without one, Mail and bounces are left out.
