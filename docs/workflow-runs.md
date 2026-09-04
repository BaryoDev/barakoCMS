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
for.

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
