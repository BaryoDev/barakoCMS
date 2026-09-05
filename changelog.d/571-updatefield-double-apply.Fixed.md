- **`UpdateFieldAction` no longer applies its change twice when an attempt is reclaimed.** The
  action wrote content in its own transaction, separate from the write that records the attempt's
  outcome. When a node ran past its lease, another node reclaimed the attempt and the first node's
  outcome was discarded on purpose (see the comment in `WorkflowRunner.TryRunAsync`), trusting the
  idempotency key to absorb the duplicate call downstream. An in-process field update has no
  downstream: the content change had already committed, the outcome was dropped, and the second
  node applied the change again with no record that it had run twice.

  The write now reloads the target immediately before deciding anything, and checks a marker on the
  content itself, keyed by the run's `IdempotencyKey` and the attempt number the runner injects.
  Two executions of the same attempt (a reclaim) find the mark already there and write nothing a
  second time; a genuine retry after a real failure carries the next attempt number, finds no
  matching mark, and still applies. The write goes through `IContentWriter.AppendOptimisticAsync`
  rather than a plain `Store`, so it does not depend on last-write-wins either.
