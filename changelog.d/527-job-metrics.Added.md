- **The job queue published no metrics.** It now publishes `barakocms_jobs_attempts_total` by
  outcome, and `barakocms_jobs_due`, `barakocms_jobs_oldest_due_age_seconds` and
  `barakocms_jobs_dead_lettered`, counted at most every `Jobs:MetricsIntervalSeconds` (30 by
  default, 0 for off). See docs/background-jobs.md.
