- **The content event stream reaches subscribers on every instance.** A change committed on one
  instance is sent to the others over Postgres `LISTEN` / `NOTIFY`, as the entry id only. Each
  instance reads the entry again and projects it through the same masking as the REST reads, in the
  tenant it was written in. No setting, no new service, and no route, field or status code changes.
  Delivery stays best effort, with no ordering across instances.
