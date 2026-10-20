- **A sweep that lost its advisory lock could repeat its work, and a held schema lock hung startup.**
  The scheduled-content and workflow run retention sweeps now check each item again inside the write,
  so a second instance that took over finds the transition already done. The schema apply lock polls
  with a timeout (5 minutes, `Schema:ApplyLockTimeoutSeconds`) and fails naming the lock key and the
  holder's pid. `docs/deploy-in-production.md` says session locks need a direct or session-pooled
  connection.
