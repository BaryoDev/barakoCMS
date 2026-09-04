- **A job queue whose enqueue shares the request's transaction.** `QueueJobAsync` stages the job in
  the request's scoped Marten session, so a request that throws or fails to commit leaves no job
  and a request that commits leaves one in its tenant. The queue owns retry: a record carries the
  attempt count, the next attempt time and the last error, waits with exponential backoff
  (`Jobs:BackoffBaseSeconds`, capped by `Jobs:BackoffMaxSeconds`) and is dead-lettered after
  `Jobs:MaxAttempts`. `GET /api/jobs` lists a tenant's jobs behind the new `view_jobs` capability,
  which Admin holds by default. Nothing migrates onto the queue yet; one logging command proves it
  runs. See `docs/background-jobs.md`.
