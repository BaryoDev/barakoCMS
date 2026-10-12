- **The metrics summary only counted since the process started.** `GET /api/monitoring/metrics`
  now also returns `startedAt`, `uptimeSeconds`, and a `last24Hours` window with requests, errors,
  error rate, average and p95 response time, and the cache hit rate (a `304` or a response the
  output cache served). The existing fields are unchanged.
