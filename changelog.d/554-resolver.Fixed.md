- **Tests that build their own host leave FastEndpoints usable.** `JobWorkerSchemaOrderTests` put
  FastEndpoints' process-wide resolver back after disposing each host, so a later test that issues
  a token outside a request no longer fails. MODULES.md says what a module's tests should do about
  the same statics.
