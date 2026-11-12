- **A module can read the needs-you items without a request.** `INeedsYouSource` returns the same
  items as `GET /api/overview` for a tenant and a set of capabilities, and `ICapabilityResolver`
  gives a module the capabilities of a user or a service principal to pass it. The overview route
  and its response are unchanged.
