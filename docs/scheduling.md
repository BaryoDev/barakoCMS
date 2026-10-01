# Scheduling publish, unpublish and sensitivity

An entry can carry three armed changes: a time to publish it, a time to unpublish (archive) it, and
a time to change its sensitivity. They are set with one call and applied by a background sweep that
runs once a minute.

## Arming a schedule

```
PUT /api/contents/{id}/schedule
Authorization: Bearer <token>

{
  "scheduledPublishAt": "2027-03-04T09:00:00Z",
  "scheduledUnpublishAt": "2027-04-04T09:00:00Z",
  "scheduledSensitivity": "Hidden",
  "scheduledSensitivityAt": "2027-05-01T00:00:00Z",
  "version": 3
}
```

Every field is optional. Times are UTC.

| Field | What it arms |
| :--- | :--- |
| `scheduledPublishAt` | Publish a Draft or Scheduled entry at or after this time |
| `scheduledUnpublishAt` | Archive a Published entry at or after this time. Has to be after `scheduledPublishAt` when both are set |
| `scheduledSensitivity`, `scheduledSensitivityAt` | Change the entry's sensitivity (`Public`, `Sensitive` or `Hidden`) at this time. Both or neither. The time has to be in the future and the level different from the current one. Both sent as `null` clears an armed change, both left out keeps it |
| `version` | The stream version the schedule was decided against. `0` or absent skips the check on a document type. An event-sourced type answers 409 when it is missing or stale |

The two times are the whole publish schedule. `scheduledPublishAt` or `scheduledUnpublishAt` that is
null or left out clears what was armed for it.

The sensitivity pair is different, because a client that only knows the publish times cannot see an
armed sensitivity change:

- both fields left out keeps the armed change as it is;
- both fields sent as `null` clears it;
- both fields sent with values arms or replaces it.

So rescheduling with only the publish times does not touch a sensitivity change armed earlier. To
clear one, send the pair as `null` explicitly:

```
{
  "scheduledPublishAt": "2027-03-04T09:00:00Z",
  "scheduledSensitivity": null,
  "scheduledSensitivityAt": null
}
```

A client whose JSON serializer drops null properties has to be told to write these two.

The caller needs the same `update` permission on the content type that a status change needs.
Otherwise the answer is 403, and 404 when the entry does not exist.

The response echoes the armed times, the armed sensitivity and the entry's `status`, because arming
can move it.

## What arming does to the status

- A Draft with a publish time becomes **Scheduled**. Clearing the publish time on a Scheduled entry
  puts it back to Draft. Both are recorded as status changes, so the history and any workflow
  watching transitions see them.
- A Published entry stays Published whatever is armed on it. An unpublish time does not unpublish
  anything until it is due.
- An Archived entry is not published by a publish time.

`GET /api/contents/{id}` reports `scheduledPublishAt`, `scheduledUnpublishAt`,
`scheduledSensitivity` and `scheduledSensitivityAt`, with a zone, and null when nothing is armed.
`GET /api/contents/{id}/history` records each arming, clearing and applied change.

## The sweep

A background service sweeps every tenant once a minute, starting 30 seconds after the host starts.
It holds a Postgres advisory lock while it runs, so with several instances only one sweeps and an
entry transitions once.

For each due entry it appends the same events a person's change would, attributed to the system
actor rather than a user:

- a due publish time publishes the entry and clears that time, keeping any unpublish time;
- a due unpublish time archives the entry and clears that time;
- a due sensitivity change applies the new level and clears it. The entry stays Published, so
  delivery, masking, change webhooks and the history see it as they see a manual change.

An entry due for a publish and a sensitivity change gets both in one sweep. A published entry
appears on the public delivery API from the sweep that publishes it, so resolution is about a minute.

Due entries are loaded 200 at a time, up to 25 batches per tenant per sweep; anything beyond that
waits for the next tick. Each entry is saved on its own under a version check, and an entry edited
by somebody else while the sweep had it loaded is skipped. Its schedule is still armed, so the next
tick picks it up against the fresh copy.

## Sensitivity versus unpublish

An unpublish time takes the entry off the public site at a date. A sensitivity change keeps it
published and limits which roles may read it from that date. Lowering sensitivity back to `Public`
on a schedule works the same way.

## Code and tests

- `barakoCMS/Features/Content/Schedule/Endpoint.cs` and `Models.cs`, the endpoint and its validation
- `barakoCMS/Infrastructure/Services/ScheduledContentService.cs`, the sweep
- `ScheduledContentTests`, `ScheduledSensitivityTests` and `MultiInstanceSchedulingTests`
