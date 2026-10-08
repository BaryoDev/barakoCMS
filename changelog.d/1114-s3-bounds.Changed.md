- **One S3 call now fits inside the job lease.** The S3 files module sets the SDK's retries and
  timeout from `Modules:Files.S3:MaxErrorRetry` (default 2, the SDK's own was 4) and
  `TimeoutSeconds` (default 45). The longest one call can take, 195 seconds with the defaults, is
  checked when the host starts against 80% of the shorter of the workflow runner's 5 minute lease
  and `Jobs:LeaseSeconds`, and settings past it stop the host with a message that names them. Each try
  now has a 45 second limit, so a large upload over a slow link may need a higher `TimeoutSeconds`
  and fewer retries (#1114).
