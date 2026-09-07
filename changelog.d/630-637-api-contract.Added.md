- **The HTTP surface is a public contract now, and it has a version.** `CLAUDE.md` section 6 used
  to exclude everything under `Features/*` on the grounds that nothing compiled against it. That
  stopped being true once barakoBrew moved to its own repository and its own release cadence: it
  reads the JSON over HTTP without ever compiling against the classes that produce it. Section 6
  now says what counts as a breaking change to that JSON (a removed or renamed field, a changed
  type, a changed status code, tightened validation) and what does not (an added optional field).
  The `Endpoint`, `Request` and `Response` types stay `internal`; only the wire shape is promised.

  `GET /api/meta` reports `ApiContractVersion` alongside the existing `Version`, and every response,
  including a 401, carries it on the `X-Api-Contract-Version` header, so a console can tell whether
  it fits before signing in and again mid-session after a rolling upgrade. The API does not declare
  a minimum supported console version; the console is the side that breaks, so it carries the range
  it works with. Closes #630, closes #637.
