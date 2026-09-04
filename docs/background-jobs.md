# Background jobs

A job is a command queued during a request and run later by a worker, with retry owned by the
queue. It is FastEndpoints' job queue with a Marten storage provider, and it is what webhook
delivery, email and AI indexing move onto in follow-ups to #106. Today one command runs on it,
`LogMessageCommand`, which writes its message to the log and exists to prove the queue runs.

## The enqueue commits with the request

```csharp
await new LogMessageCommand { Message = "hello" }.QueueJobAsync(ct: ct);
await _session.SaveChangesAsync(ct);
```

`QueueJobAsync` stages the job in the request's scoped `IDocumentSession`, the same one the endpoint
writes content with. The job commits when the endpoint calls `SaveChangesAsync`, and not otherwise.
`TransactionalEnqueueTests` proves both directions: a request that throws after queueing leaves no
job, a request whose commit fails on a unique index leaves no job, and a request that commits leaves
one in the tenant it ran in.

Two rules follow, and both are on the endpoint. Queue from a request that writes through the scoped
session, and call `SaveChangesAsync` after queueing. An endpoint that queues and never saves discards
the job; the provider logs a warning naming the request when that happens on a successful response.

Outside a request, from a background service, the job is written and committed on its own in the
default tenant. A background caller that needs another tenant is a follow-up.

## Handlers run outside a request

A handler is built from the root container on a worker. Take singletons, or `IDocumentStore` and
open a session for the tenant the command names. Asking for a scoped `IDocumentSession` fails at
construction. A command carries what its handler needs, including the tenant.

## Retry

```json
{
  "Jobs": {
    "MaxAttempts": 5,
    "BackoffBaseSeconds": 30,
    "BackoffMaxSeconds": 3600,
    "StorageProbeSeconds": 60
  }
}
```

Those are the defaults. A handler that throws counts one attempt. The next attempt waits the base,
then twice that, then four times, capped at the max: 30 seconds, 1 minute, 2, 4, 8, up to an hour.
After `MaxAttempts` failures the job is dead-lettered and nothing picks it up again. `MaxAttempts` is
copied onto the record when the job is queued, so changing it does not move the goalposts on a job
already in flight. A retry the queue planned pushes the job's expiry past the next attempt, so a
long backoff cannot expire the job before it runs.

`StorageProbeSeconds` is how often a worker re-reads storage for jobs it was not told about: a retry
that came due, or a job another instance queued. A committed enqueue wakes the worker on its own, so
this is the latency of retries and of cross-instance pickup, not of an ordinary enqueue.

The stored error is the exception's type and message, cut at a thousand characters. Never a stack
trace, and never a response body, because a body is where a credential turns up.

## States

| State | Meaning |
| --- | --- |
| `Pending` | Stored and waiting for `ExecuteAfter`, or waiting for its next attempt |
| `Running` | Claimed by a worker. The lease is `DequeueAfter`; a crash frees the job when it passes |
| `Completed` | The handler returned. Deleted by the hourly purge |
| `DeadLettered` | Failed `MaxAttempts` times, expired before it ran, or was cancelled. Kept |

## Reading the queue

`GET /api/jobs` lists the tenant's jobs newest first, paginated, with `?state=` as a filter that
refuses a state it does not know. It needs `view_jobs`, which Admin holds by default. A row carries
the command's type, the attempt count, when the next attempt is due and the last error. It never
carries the command's payload, because a queued email is an address and a body.

## Several instances

Every instance runs workers. A claim is a load, a lease and a save under Marten's optimistic
concurrency, so two instances polling the same table cannot both run one job. A job belongs to the
tenant of the request that queued it, and a worker serves every tenant.
