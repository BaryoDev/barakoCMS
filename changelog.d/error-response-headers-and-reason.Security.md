- **A 500 carried the exception's message, and early error responses lost the security headers.**
  The global handler put the exception message in the body for any caller, anonymous included; it
  now answers with a fixed reason and keeps the detail in the log. The 413, 400 and 503 refusals
  clear the response before writing, which removed `X-Api-Contract-Version`,
  `X-Content-Type-Options`, the CSP and, on auth paths, `no-store`; the 500 lost the first three. Those headers are now written as the response
  starts, so every response carries them. The response shape is unchanged.
