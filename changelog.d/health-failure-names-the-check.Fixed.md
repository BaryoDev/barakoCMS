- **The health canary pins the shape of the `/health` body instead of asserting the app is healthy.**
  It exists so a dashboard or a kubelet parsing that body sees what it always saw, and its own
  comment already said the assertion was about the shape rather than about when seeding ends. It
  asserted the status word was `Healthy` anyway, which made it depend on the startup seed finishing
  inside a fixed window on a shared CI runner. It now accepts any of the three status words and
  still fails on a new field, a renamed property or added whitespace, which is what it is for. No
  production code changed.
