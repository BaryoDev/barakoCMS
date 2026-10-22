- **A connector send no longer buffers the whole response.** The body is read only as far as the
  delivery row keeps it, so a provider that never stops sending cannot fill memory.
