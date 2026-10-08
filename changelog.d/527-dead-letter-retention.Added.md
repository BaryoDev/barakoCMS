- **Dead-lettered and cancelled jobs were never deleted.** An hourly sweep now deletes them once
  they have been given up on for longer than `Jobs:DeadLetterRetentionDays`, 90 by default, the
  window a failed workflow run is kept for. Zero or less keeps them forever, as before. A job that
  dead-letters now records when it gave up in `completedAt`.
