# Workflow runs and how long they are kept

Every time a workflow fires it leaves a `WorkflowRun` behind: what it decided to do, every attempt at
each action, and how each went. They accumulate at the rate content is published times the number of
actions per workflow, so something has to remove them.

## The two windows

Failures are kept longer than successes, because they are interesting for longer. A run that
succeeded answers "did that go out" for a while. A run that failed is interesting until somebody
deals with it.

```json
{
  "Workflows": {
    "Retention": {
      "Succeeded": 7,
      "Failed": 90
    }
  }
}
```

Both are in days and both are the defaults, so a deployment that sets neither gets seven and ninety.
`Workflows:Retention:Enabled` turns the sweep off entirely, and then runs accumulate.

`PartiallyFailed` is kept on the failure window, not the success one. A run where the post went out
and the email did not holds a thing nobody has dealt with, which is the case the longer window is
for. `Cancelled` is kept on the failure window too: it holds an action that was stopped, and
whoever asks why wants it for as long as a failure.

**Zero or less keeps that class forever.** That reading was chosen deliberately, because "0 days"
reads as "delete immediately" just as naturally, and a setting whose two plain readings are opposite
should not be settled by a default. Keeping is the direction a mistake can be recovered from.

## What is never removed

A run that is `Pending` or `Running` is never removed, whatever its age. That is a rule rather than a
consequence of the windows: a run whose provider has been unreachable for a fortnight is still an
email somebody is waiting for, and a window would otherwise delete the work rather than the record of
it.

If runs are piling up in `Pending`, the runner is not keeping up or is switched off
(`Workflows:RunnerEnabled`). Retention is not the thing to reach for.

Webhook deliveries keep their own log with its own window, `Webhooks:DeliveryLogRetentionDays`.
See `docs/webhooks.md`.

## How many actions run at once

A node runs one action at a time unless it is told otherwise. Every action is a call to a third
party, so at two seconds per webhook that is half an action a second per node, and a bigger machine
does not change it.

```json
{
  "Workflows": {
    "RunnerConcurrency": 4
  }
}
```

`Workflows:RunnerConcurrency` is how many actions one node may have in flight at once. The default
is 1. It takes 1 to 20, and a value outside that range stops the API from starting, with an error
that names the setting. Twenty is the number of due runs a pass reads per tenant.

What to know before raising it:

- **The bound is per node.** The worst case against one provider is the setting times the number of
  nodes: three nodes at 4 is twelve calls to the same provider in flight at once. Set it from the
  provider's rate limit, not from the node's cores. A provider that starts answering 429 turns the
  extra calls into retries.
- **The actions of one run still run in order, one at a time.** "Post, then email, then tweet" stays
  a sequence. Only actions of different runs overlap.
- **Runs no longer finish in the order they were queued.** At 1, a node works through a tenant's
  runs oldest first, so two runs for the same entry reach a receiver in the order they fired. Above
  1 they can be in flight together and arrive either way round. Several nodes already had this.
- **Two runs that write the same entry can collide.** Two workflows on one event, each with an
  `UpdateField` on the entry that triggered them, can be claimed in the same pass. Both load the
  entry, one write is refused, and that action spends one of its five attempts and waits out its
  backoff. It succeeds on the retry. Several nodes at 1 already behave this way.
- **Tenants share the slots.** A pass hands them out one per tenant in turn, so a tenant with a
  long queue gets a second slot only after every other tenant with due work has had one.
- **A pass waits for all of its actions before it claims again.** One slow action holds the other
  slots of its pass empty until it ends, so throughput is the bound divided by the slowest action
  of each pass, not by the average. The slowest action of any tenant sets the length of the pass
  for every tenant.
- **Custom actions run side by side.** Above 1, actions of different runs execute at the same time
  in one process, so a custom action that keeps state outside its own instance has to be safe for
  that.
- Each action in flight uses a database connection while it loads the entry and records the
  outcome, so the setting also has to fit the connection pool.

When a node stops, the actions it has in flight are cancelled and the node waits for them to end,
the same as with one. An attempt whose outcome was not recorded stays `Running` under its lease and
is taken again, by any node, when the lease ends five minutes after it was claimed.

## Stopping a chain at a failure

By default a failed action does not stop the ones after it. "Post, then email, then tweet" is three
independent things, the tweet still goes out when the mail server is down, and the run reads
`PartiallyFailed`.

A chain is different: if the journal entry fails, filing the document and adjusting stock should not
go ahead as though it had worked. Each action of a workflow takes `onFailure`, `Continue` or `Halt`.
Left out or sent as `null` it is `Continue`, and so is every action saved before the setting
existed. Any other value is refused with a 400, where the API used to ignore the field.

```json
{
  "actions": [
    { "type": "Webhook", "parameters": { "Url": "https://ledger.example.com/entries" }, "onFailure": "Halt" },
    { "type": "Webhook", "parameters": { "Url": "https://stock.example.com/adjust" } }
  ]
}
```

What `Halt` does:

- **Nothing after the action runs until it has succeeded.** While it waits on a retry, the actions
  after it wait too. A `Continue` action in the same position lets them run in the meantime.
- **Once it fails for good, the actions after it that have not run become `Skipped`.** That is a
  failure that is not retried, or the fifth failed attempt. Each skipped action carries `haltedBy`,
  the ordinal of the action that stopped it, and the run is finished: `Failed` if nothing of it
  succeeded, `PartiallyFailed` otherwise.
- **`Unknown` halts as well.** A timeout does not say whether the step happened, and the steps after
  it would run as though it had.
- **An action skipped because the content was deleted does not halt.** It did not fail.
- **Actions before the halting one are not touched.** One of them still waiting on its own retry
  keeps it.

`POST /api/workflow-runs/{id}/actions/{ordinal}/retry` on the action that halted the run is the
resume: fix the cause, retry it, and the actions it skipped are queued again behind it. They run
once it succeeds, and are skipped again if it fails again. The audit entry of that retry carries
`resumedActions`, the number it queued again. Retrying one of the skipped actions on its own answers
409, because that would run it past the failure.

The policy is copied onto a run when the workflow fires, like the action's parameters, so a run
already queued keeps the policy it was queued with. It applies to a workflow's own actions. The
children of a `Conditional` have no policy of their own: the `Conditional` succeeds or fails as one
action, and its own `onFailure` decides what follows. A workflow that puts `onFailure` on a child
is refused when it is saved.

The engine a host can call in line through `IWorkflowEngine` follows the policy too. It retries
nothing, so any failure of a `Halt` action ends the workflow there, and the actions after it are
written to the execution log as not run.

A node on a version before this one does not know the policy. It runs past a failed or waiting
`Halt` action, and when it writes a run it drops `onFailure` and `haltedBy` from it, so a halted
run retried through such a node can end `Succeeded` with actions that never ran. That is true
during a rolling upgrade and after a rollback alike. Finish the rollout before saving a workflow
that uses `Halt`, and before rolling back past this version, change those workflows back or wait
for their runs to finish.

## Stopping a workflow or a run

Three requests, all behind `manage_workflows`, each written to the audit log.

`PUT /api/workflows/{id}/enabled` with `{ "enabled": false }` switches a workflow off. It starts no
more runs, on any trigger. Runs it had already queued are cancelled by the runner as it reaches
them, which for a run waiting on a backoff is when the wait ends, and switching the workflow back on
does not bring them back. A workflow saved before the flag existed is on. While a workflow is off, a
retry of one of its failed actions answers 409: the runner would cancel the run where it should run
it. Switch the workflow on, then retry.

`DELETE /api/workflows/{id}` deletes a workflow and cancels its queued runs in the same transaction.
It cancels at most 200 runs. A workflow with more than that queued is refused with a 409: switch it
off first, let the runner cancel them, then delete it. Its finished runs stay as history, and a
retry of one of their actions answers 409, so a deleted workflow sends nothing more. A delete and a
retry of one of the workflow's runs take turns: a retry that arrives during the delete waits and is
then refused, and a delete that arrives during a retry waits and cancels the attempt the retry
queued. A run that finishes while the delete is under way keeps the status it finished with. One gap
is left:
a run queued by an event in the instant between the delete reading the queue and committing is not
cancelled, and executes once with the actions it copied. Switching the workflow off before deleting
it closes that.

`POST /api/workflow-runs/{id}/cancel` stops one run. Every action that has not started becomes
`Cancelled`, which always means the action never went out. An action that is running is left,
because its request is already with the third party: it finishes and records its outcome as it
happened, nothing after it starts, and if it failed it is not tried again. An action that was
claimed by a node that then went quiet, so its lease ran out with no outcome, becomes `Unknown`: it
may have gone out. The run reads `Cancelled` once nothing of it is in flight, whatever its actions
did. A run that has finished cannot be cancelled, and an action of a cancelled run cannot be retried.
Both answer 409. Cancelling a run that is already stopped and still has an action out changes
nothing and records nothing.

A cancel and the runner claiming the same run cannot both be saved. If the runner got there first the
cancel answers 409 and can be sent again for what is left.

## Watching the runner

The queue and the runner publish to `/metrics`, the Prometheus endpoint, which needs
`Metrics:ScrapeKey` (see `docs/upgrading-to-4.0.md` for the scrape config). These are the numbers
that answer "are workflows running" without a query against the database.

| Metric | Type | Labels | What it counts |
| --- | --- | --- | --- |
| `barakocms_workflow_runs_queued_total` | counter | `trigger` | Runs queued, by the kind of event that fired them. |
| `barakocms_workflow_attempts_claimed_total` | counter | none | Action attempts this node's runner claimed. |
| `barakocms_workflow_attempts_total` | counter | `action`, `outcome` | Attempts whose outcome this node recorded. |
| `barakocms_workflow_action_duration_seconds` | histogram | `action` | How long the action took, for those attempts. |
| `barakocms_workflow_runs_finished_total` | counter | `status` | Runs this node's runner finished. |
| `barakocms_workflow_runs_halted_total` | counter | none | Runs where a `Halt` action failed and later actions were skipped. |
| `barakocms_workflow_runner_last_pass_timestamp_seconds` | gauge | none | Unix time this node's runner last completed a pass. |
| `barakocms_workflow_due_runs` | gauge | none | Runs with an action that could be claimed now. |
| `barakocms_workflow_oldest_due_run_age_seconds` | gauge | none | Seconds since the oldest due run was queued. 0 when none is due. |
| `barakocms_workflow_backlog_measured_timestamp_seconds` | gauge | none | Unix time the two gauges above were last measured. |

The label values are fixed, so the number of series is too:

- `trigger` is `created`, `updated`, `deleted`, `published`, `unpublished`, `transition` or `other`.
  Every lifecycle transition is counted as `transition`, whatever it is called. 7 series at most.
- `outcome` is `succeeded`, `failed`, `retried`, `unknown` or `skipped`. `retried` is a failure the
  runner queued again, and `failed` is one it did not. `skipped` is the content having been deleted
  before the action ran. An action skipped behind a `Halt` was never attempted and is not counted
  here.
- `action` is the type of a registered action (`Email`, `Webhook` and so on, and any custom action
  the host registers), or `other`. A type no handler is registered for is counted as `other`, and so
  is every registered type past the first 50 a node sees. So `barakocms_workflow_attempts_total` is
  at most 51 actions times 6 outcomes (the five above and a spare `other`), 306 series, and a host
  with the seven built in actions has up to 40. The duration histogram has 15 series per action (12
  buckets from 50 ms to 5 minutes, `+Inf`, sum and count), 765 at most.
- `status` is `succeeded`, `failed`, `partially_failed` or `cancelled`. 4 series.

No label holds a tenant, a workflow, a run, a content type or an error message, and no metric is
kept per tenant. Whoever can read `/metrics` learns how much workflow activity the whole deployment
has, which action types it uses, how long they take and how often they fail. That is the same kind
of exposure as the request metrics already there, which name every route and count its traffic.

What the numbers do not include:

- `runs_finished_total` counts a run when the runner writes its last outcome, or cancels what is
  left of it. A run cancelled through the API with nothing in flight is finished by the API and is
  not counted. A run that finishes, is retried by hand and finishes again is counted twice.
- An attempt is counted when its outcome is saved. An outcome that could not be saved (the node ran
  past its lease, or the run was written twice in between) is claimed but not counted, so
  `attempts_claimed_total` running ahead of `attempts_total` by more than what is in flight is a sign
  of that.
- The counters are per node and start at zero when the node starts. Sum them across nodes.

### The backlog gauges

`due_runs` and `oldest_due_run_age_seconds` are measured by the runner between passes, at most once
every 30 seconds, and never by a scrape. A scrape reads the last numbers. Each measurement is one
count per tenant partition, the same read a pass already makes to find what is due, and one more
read of a single run in a partition that has something due. It stops after 10 seconds. With
`Tenancy:DatabaseEnforcement` on, that is one count per registered tenant, as a pass is.

If a measurement fails or runs out of time, nothing is published, the node logs a warning, and the
gauges keep their last values. The runner goes on to its next pass either way. That is why the time
of the measurement is published beside them: old numbers are told apart by their age.

Every node measures the same database, so take `max` across nodes, not `sum`.

A run that failed and is waiting on a retry is not due until its wait ends, and its age is counted
from when it was first queued. So a run on its fifth attempt reads a few minutes old the moment it
comes due. Set the age threshold above that.

A node with `Workflows:RunnerEnabled` off publishes none of the four gauges.

### Alerts

The runner has stopped, or every pass is failing. A pass waits for its actions, so the threshold
has to be longer than the slowest action:

```
time() - max(barakocms_workflow_runner_last_pass_timestamp_seconds) > 600
```

No node has completed a pass since it started:

```
absent(barakocms_workflow_runner_last_pass_timestamp_seconds)
```

Work is due and is not being taken. This is the one that catches a runner that is alive and not
keeping up:

```
max(barakocms_workflow_oldest_due_run_age_seconds) > 900
```

The backlog numbers are old, so the alert above cannot be trusted:

```
time() - max(barakocms_workflow_backlog_measured_timestamp_seconds) > 300
```

More than half the attempts of one action type are failing, which is usually a provider being down:

```
sum by (action) (rate(barakocms_workflow_attempts_total{outcome=~"failed|retried|unknown"}[10m]))
  / sum by (action) (rate(barakocms_workflow_attempts_total[10m])) > 0.5
```

Do not alert on queued against finished. A run cancelled through the API is queued and never
counted as finished, and so is a run of a workflow with no actions.

## This is not an audit trail

Say it plainly, because a retention setting is exactly the kind of thing that quietly becomes a
compliance control.

The sweep removes operational records. The audit entries a retry writes are separate documents and
are not touched by any of this. If you need workflow history kept for longer than the operational
window, export it: a longer retention window is a database that grows without bound, and it is still
not an audit trail, because a run can be deleted by an operator changing a setting and nothing
records that it was.

## How it runs

Hourly, on one instance at a time. It takes a Postgres advisory lock the way the scheduled content
sweep does, so a two-node deployment does not have both nodes deleting the same batch. Deletion is
batched and bounded, five hundred runs per batch and twenty batches per tick, so a backlog is worked
through over a few hours rather than in one transaction that holds a connection all night.

The first sweep waits two minutes after start, so a rollout finishes booting before anything is
deleted.
