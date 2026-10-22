- **A connector send no longer buffers the whole response.** The body is read only as far as the
  delivery row keeps it, within 30 seconds of the headers, so a provider that never stops sending or
  stops mid-body cannot hold the run.
