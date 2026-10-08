- **One S3 call now fits inside the job lease.** The S3 files module sets the SDK's retries and
  timeout from `Modules:Files.S3:MaxErrorRetry` (2 when unset, the SDK's own was 4) and
  `TimeoutSeconds` (45 when unset). The longest one call can take, 195 seconds with the defaults,
  must fit in 80% of the shorter of the workflow runner's 5 minute lease and `Jobs:LeaseSeconds`.
  An unset value gives way to fit a shorter lease, so a host that started before still starts;
  values set by hand that cannot fit stop the host with a message that names them. Each try now has
  a time limit, so a large upload over a slow link may need a higher `TimeoutSeconds` and fewer
  retries. `MaxErrorRetry` is always set on the client now, so `AWS_MAX_ATTEMPTS` no longer changes
  it, and `AWS_RETRY_MODE=adaptive` can wait for the SDK's rate limiter outside this bound (#1114).
