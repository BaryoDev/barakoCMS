- **A role named `SuperAdmin`, `Admin`, `HR` or `User` is now refused with 400.** That tightens
  request validation on `POST /api/roles` and `PUT /api/roles/{id}`, so `X-Api-Contract-Version`
  moves to 3. A seeded role keeps its own name, so renaming one to what it already is still works.
  The names were not previously reserved by anything, so an existing custom role holding one keeps
  working and keeps serving; the next edit of it through the role endpoint is refused until its name
  is changed. The barakoBrew console refuses to start against a contract version it does not speak,
  so it has to be upgraded in lockstep with this release. See the Security entry for why the names
  had to become unavailable rather than just unprivileged.
