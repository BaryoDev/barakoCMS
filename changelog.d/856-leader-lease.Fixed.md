- **A sweep that lost its advisory lock kept sweeping.** The scheduled-content sweep and the
  retention sweeps (workflow runs and webhook deliveries) now ask Postgres whether they still own
  their lock before each tenant and stop if not, reporting the lock as not held. The schema apply
  lock, the scheduled-content sweep and the retention sweeps share one lease that serialises use of
  the lock connection and closes it on any failure. It also looks for a transaction-mode pooler (two
  statements on different backends), best effort, since a quiet pooler can hand both to one backend.
  A suspected pooler logs an error once per process. `Leadership:RefuseBehindPooler` (default false,
  so nothing changes) makes the sweeps decline and the schema lock refuse to start.
