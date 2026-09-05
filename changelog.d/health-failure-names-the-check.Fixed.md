- **The health canary test now names the check that failed, in both of its failure modes.** It
  asserts that `/health` still answers `{"status":"Healthy"}`, and that body is deliberately terse,
  so every failure reported either a boolean or a string mismatch at index 11 and nothing about
  which of the database, disk, memory, seed or projection checks was the cause. It now resolves
  `HealthCheckService` and lists every failing entry with its description, whether readiness never
  opened or readiness opened while another check was still unhealthy. No production code changes.
