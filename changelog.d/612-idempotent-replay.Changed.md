- **A retry with a used `Idempotency-Key` got a 409 instead of the response it missed.** A client
  whose connection dropped after the write committed learned that it had succeeded and never learned
  the id it created. The same request from the same authenticated caller now gets the first
  response replayed: its status code, `Content-Type`, `Location` and body, plus
  `Idempotent-Replayed: true`, and the handler does not run again. The same key with a different
  method, path, query string or body is answered 422. A key whose first request is still running
  stays a 409. A response over `Idempotency:MaxStoredResponseBytes` (default 65536) is not kept, and
  a retry of it stays a 409 saying so. Routes that answer with a credential (sign-in, refresh, OTP and
  MFA verify, tenant switch, MFA setup and enable, API key, preview token and share link create)
  never store or replay a response and keep the 409; an endpoint opts in with
  `[NoIdempotentReplay]`. Unauthenticated callers keep deduplication only: a retry is a 409 and
  nothing about the request is stored. Stored bodies are encrypted with the stored-secret key. Only
  a 2xx is kept; any other status releases the key, which now includes 3xx. An API key is now its own
  caller, so it no longer shares keys with the user it acts for. This is a status change on every
  `POST`, `PUT` and `PATCH`, covered by `ApiContract.Version` 7. Keys are honoured for
  `Idempotency:KeyHours` (default 24, 1 to 720) and are free again after that, checked on the
  request rather than waiting for the hourly sweep that used to be the only expiry. A deployment
  that relies on keys lasting longer sets `Idempotency:KeyHours`, up to 720 (30 days). Expired
  records are deleted by their own hourly sweep, which runs again after a minute and logs a warning
  when it could not finish. See `docs/idempotency.md`.
