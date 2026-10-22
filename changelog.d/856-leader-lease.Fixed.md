- **A sweep that lost its advisory lock kept sweeping.** The scheduled-content and workflow run
  retention sweeps now ask Postgres whether they still own their lock before each tenant and stop
  if not. All three advisory locks share one lease that serialises use of the lock connection, and
  it detects a transaction-mode pooler (two statements on different backends) and logs an error.
  `Leadership:RefuseBehindPooler` (default false, so nothing changes) makes the sweeps decline and
  the schema lock refuse to start in that case.
