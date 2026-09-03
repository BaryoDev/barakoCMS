- **CI runs on the merge queue.** `ci.yml` gains a `merge_group` trigger, without path filters,
  because the queue is the last gate before master and a required check only counts when it reports
  on that event. Without it the queue waits forever for checks that never start.
