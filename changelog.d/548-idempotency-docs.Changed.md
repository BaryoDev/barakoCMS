- **Inbound idempotency is documented.** `IdempotencyFilter` has honoured an `Idempotency-Key`
  header on `POST`, `PUT` and `PATCH` since before this entry, but the only header named
  `Idempotency-Key` anywhere in `docs/` was the outbound one on webhook deliveries, a different
  thing entirely. Nobody sending the header meant the protection sat unused. `docs/idempotency.md`
  now covers the header name, the verbs it applies to, the exact 409 a replay gets, how long a
  completed key is remembered (indefinitely; a failed one is released immediately), and what happens
  when two requests race on the same key. Linked from the README's documentation list.

  `IdempotencyTests` now also posts to `/api/contents` twice with the same key and checks that only
  one entry landed, not only that the second call's status code was 409.
