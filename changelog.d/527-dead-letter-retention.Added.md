- **Dead-lettered and cancelled jobs were never deleted.** Set `Jobs:DeadLetterRetentionDays` (90 is
  the recommended value, at most 3650) to turn on an hourly sweep that deletes them once they have
  been given up on for longer than that. It is off by default, so a deployment keeps every dead
  letter as before until it opts in. A job that dead-letters now records when it gave up in
  `completedAt`.
